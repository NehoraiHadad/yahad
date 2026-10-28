import {test} from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {randomBytes} from 'node:crypto';
import {Store} from '../src/store.js';
import {createApp} from '../src/server.js';
import {BaileysConnector} from '../src/connectors/baileys.js';
import {MockConnector} from '../src/connectors/mock.js';
import {IngestionQueue} from '../src/connectors/ingestion.js';
import {HistoryTracker} from '../src/history.js';
import type {Config} from '../src/config.js';
import type {Incoming,StoredMessage,ConnectorHooks,HistoryNotice} from '../src/connectors/types.js';

const g='120363001@g.us',other='120363002@g.us';
const raw=(id:string,group=g,time=Date.now()-10000,fromMe=false)=>({key:{remoteJid:group,id,fromMe},messageTimestamp:Math.floor(time/1000),message:{conversation:'synthetic history'}});
const message=(id:string,group=g,time=Date.now()-10000,fromMe=false):StoredMessage=>({id,groupId:group,sender:'Synthetic',fromMe,time:Math.floor(time/1000)*1000,kind:'text',text:'synthetic history',status:'sent',reactions:{},raw:JSON.stringify(raw(id,group,time,fromMe))});
class HistoryMock extends MockConnector {
  calls: StoredMessage[]=[];
  result:()=>Promise<string|void>=async()=> 'request-one';
  constructor(h:ConnectorHooks){super(h);this.capabilities.historyRequest=true;}
  override async approved(){}
  historyAnchor(group:string,m:StoredMessage){return BaileysConnector.prototype.historyAnchor.call({} as any,group,m);}
  override async history(_g:string,m:StoredMessage){this.calls.push(m);return this.result();}
}
async function fixture(){
  const dir=mkdtempSync(join(tmpdir(),'yachad-history-'));
  const config:Config={dataDir:dir,masterKey:randomBytes(32),connector:'mock',realAllowed:false,host:'127.0.0.1',port:3000,origin:'http://localhost:3000',secureCookie:false,maxMedia:1048576,maxStorage:67108864,childIdleMs:900000,sessionMs:3600000,diagnosticsEnabled:false};
  const store=new Store(config),pass=randomBytes(24).toString('hex');
  const a=await store.createFamily('A',{name:'parent-a',password:pass},{name:'child-a',password:pass+'a'});
  const b=await store.createFamily('B',{name:'parent-b',password:pass},{name:'child-b',password:pass+'b'});
  const {app,service}=await createApp(config,{store,factory:h=>new HistoryMock(h)});
  await service.connect(a.familyId);await service.connect(b.familyId);
  await service.grant(a.familyId,a.childId,g,true);await service.grant(b.familyId,b.childId,other,true);
  const child=store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?',a.childId)));
  const req=(group=g,method:any='GET',body?:any)=>app.inject({method,url:'/api/groups/'+encodeURIComponent(group)+'/history',headers:{origin:config.origin,cookie:'sid='+child.sid,'x-csrf-token':child.csrf},payload:body});
  return{store,app,service,a,b,req,c:service.connector(a.familyId) as HistoryMock,
    seed:(m:StoredMessage)=>store.putMessage(a.familyId,m),async close(){await app.close();store.close();rmSync(dir,{recursive:true,force:true});}};
}

test('approval after connect stays honestly unavailable without an anchor; requests cannot target other groups or supply anchors',async()=>{
  const f=await fixture();try{
    const status=(await f.req()).json();assert.equal(status.reason,'no_anchor');assert.equal(status.requestable,false);assert.equal(status.totalStored,0);
    assert.equal((await f.req(g,'POST',{})).json().reason,'no_anchor');assert.equal(f.c.calls.length,0);
    for(const method of ['GET','POST'])assert.equal((await f.req(other,method,method==='POST'?{}:undefined)).statusCode,404);
    assert.equal((await f.req(g,'POST',{oldestId:'foreign-anchor'})).statusCode,400);
  }finally{await f.close();}
});

test('server chooses the oldest usable original outgoing/received anchor, skipping malformed/deleted/unsupported/pending rows',async()=>{
  const f=await fixture();try{
    const time=Date.now()-30000;
    for(const [i,patch] of [{raw:'invalid'},{deleted:true},{kind:'unsupported'},{status:'pending'},{raw:JSON.stringify(raw('wrong',other,time))}].entries())
      f.seed({...message('bad-'+i,g,time+i*1000),...patch} as StoredMessage);
    f.seed(message('old-outgoing',g,time+6000,true));f.seed(message('new-received',g,time+7000));
    const result=(await f.req(g,'POST',{})).json();assert.equal(result.phase,'waiting');assert.equal(result.complete,false);assert.equal(result.ok,undefined);
    assert.equal(f.c.calls[0].id,'old-outgoing');assert.equal(f.c.calls[0].fromMe,true);
    await f.req(g,'POST',{});assert.equal(f.c.calls.length,1);
  }finally{await f.close();}
});

test('identical group IDs remain isolated between families and bounded anchor scans report their limit',async()=>{
  const f=await fixture();try{
    await f.service.grant(f.b.familyId,f.b.childId,g,true);f.store.putMessage(f.b.familyId,message('private-b-anchor'));
    assert.equal((await f.req()).json().totalStored,0);assert.equal((await f.req(g,'POST',{})).json().reason,'no_anchor');assert.equal(f.c.calls.length,0);
    f.store.tx(()=>{for(let i=0;i<2049;i++)f.seed({...message('pending-'+i,g,Date.now()-300000+i*1000),status:'pending'});});
    const status=(await f.req()).json();assert.equal(status.reason,'anchor_scan_limit');assert.equal(status.requestable,false);assert.equal(status.totalStored,2049);
  }finally{await f.close();}
});

test('submitted request is distinct from stored history; duplicates and unmatched events do not invent restoration',async()=>{
  const f=await fixture();try{
    f.seed(message('anchor'));await f.req(g,'POST',{});
    const h=f.c.hooks;
    const batch=(phase:'start'|'end',id:string,n=1)=>h.history!({groupId:g,history:true,requestId:id,phase,received:n,processed:phase==='end'?n:0,final:phase==='end'});
    batch('start','different');await h.incoming({...message('unmatched'),history:{requestId:'different'}});batch('end','different');
    let s=(await f.req()).json();assert.equal(s.phase,'waiting');assert.equal(s.stored,1);assert.equal(s.requestStored,0);assert.equal(s.correlated,false);
    batch('start','request-one',2);for(let i=0;i<2;i++)await h.incoming({...message('matched'),history:{requestId:'request-one'}});batch('end','request-one',2);
    s=(await f.req()).json();assert.equal(s.phase,'partial');assert.equal(s.stored,2);assert.equal(s.requestStored,1);assert.equal(s.totalStored,3);assert.equal(s.complete,false);
    batch('start','request-one');await h.incoming({...message('second-chunk'),history:{requestId:'request-one'}});batch('end','request-one');
    assert.equal((await f.req()).json().requestStored,2);
  }finally{await f.close();}
});

test('response before request ID resolution correlates only when the exact supported field matches',async()=>{
  const f=await fixture();let finish!:(id:string)=>void;try{
    f.seed(message('anchor'));let started!:()=>void;const waiting=new Promise<void>(r=>started=r);
    f.c.result=()=>{started();return new Promise<string>(r=>finish=r);};const pending=f.req(g,'POST',{});await waiting;
    f.c.hooks.history!({groupId:g,history:true,requestId:'early',phase:'start',received:1,processed:0});
    await f.c.hooks.incoming({...message('early-message'),history:{requestId:'early'}});
    f.c.hooks.history!({groupId:g,history:true,requestId:'early',phase:'end',received:1,processed:1,final:true});
    finish('early');const result=(await pending).json();assert.equal(result.phase,'partial');assert.equal(result.requestStored,1);assert.equal(result.correlated,true);
  }finally{finish?.('early');await f.close();}
});

test('exact-ID empty responses are zero-stored partial replies, including early responses; unmatched empties are not replies',async()=>{
  const f=await fixture();try{
    f.seed(message('anchor'));await f.req(g,'POST',{});f.c.hooks.historyEmpty!('unmatched');assert.equal((await f.req()).json().phase,'waiting');
    f.c.hooks.historyEmpty!('request-one');let status=(await f.req()).json();assert.equal(status.phase,'partial');assert.equal(status.correlated,true);assert.equal(status.requestStored,0);assert.equal(status.complete,false);
    f.service.histories.forget(f.a.familyId,g);let finish!:(id:string)=>void,started!:()=>void;const waiting=new Promise<void>(r=>started=r);
    f.c.result=()=>{started();return new Promise<string>(r=>finish=r);};const pending=f.req(g,'POST',{});await waiting;f.c.hooks.historyEmpty!('early-empty');finish('early-empty');
    status=(await pending).json();assert.equal(status.phase,'partial');assert.equal(status.correlated,true);assert.equal(status.stored,0);
  }finally{await f.close();}
});

test('history failures and response/submission timeouts remain partial and retry is bounded',{timeout:15000},async t=>{
  const f=await fixture();try{
    f.seed(message('anchor'));f.c.result=async()=>{throw new Error('PRIVATE_PROVIDER_ERROR');};
    let result=(await f.req(g,'POST',{})).json();assert.equal(result.phase,'failed');assert.ok(!JSON.stringify(result).includes('PRIVATE'));
    assert.equal((await f.req(g,'POST',{})).statusCode,429);
    t.mock.timers.enable({apis:['Date','setTimeout'],now:Date.now()});t.mock.timers.tick(15001);f.c.result=async()=> 'request-two';
    result=(await f.req(g,'POST',{})).json();assert.equal(result.phase,'waiting');t.mock.timers.tick(45001);
    result=(await f.req()).json();assert.equal(result.phase,'timeout');assert.equal(result.complete,false);
    let started!:()=>void;const waiting=new Promise<void>(r=>started=r);
    f.c.result=()=>{started();return new Promise(()=>{});};const pending=f.req(g,'POST',{});await waiting;t.mock.timers.tick(10001);
    assert.equal((await pending).json().phase,'timeout');
  }finally{await f.close();}
});

test('revoking permission while history submission waits prevents status/count disclosure',async()=>{
  const f=await fixture();let finish!:(id:string)=>void;try{
    f.seed(message('anchor'));let started!:()=>void;const waiting=new Promise<void>(r=>started=r);
    f.c.result=()=>{started();return new Promise<string>(r=>finish=r);};const pending=f.req(g,'POST',{});await waiting;
    await f.service.grant(f.a.familyId,f.a.childId,g,false);finish('late');assert.equal((await pending).statusCode,404);
    assert.equal((await f.req()).statusCode,404);
  }finally{finish?.('late');await f.close();}
});

function adapterFixture(budget={active:0}){
  const allowed=new Set([g]),received:Incoming[]=[],notices:HistoryNotice[]=[],listeners=new Map<string,Function[]>();let epoch='first',configuration:any;
  const auth=new Map<string,string>();
  const h:ConnectorHooks={allowed:x=>allowed.has(x),retentionToken:()=>epoch,history:n=>notices.push(n),incoming:async m=>{if(!m.guard||m.guard())received.push(m);},state(){},update(){},typing(){},authGet:k=>auth.get(k),authSet(k,v){if(v===null)auth.delete(k);else auth.set(k,v);},authBatch(v){for(const[k,x]of Object.entries(v))h.authSet(k,x);},getMessage(){return undefined;},maxMedia:1048576};
  const sock:any={ev:{on(name:string,fn:Function){listeners.set(name,[...(listeners.get(name)||[]),fn]);}},end(){},logout:async()=>{},fetchMessageHistory:async()=> 'provider-request'};
  const connector=new BaileysConnector(h,((c:any)=>{configuration=c;return sock;})as any,budget);
  const emit=(name:string,event:any)=>{for(const fn of listeners.get(name)||[])fn(event);};
  return{connector,h,allowed,received,notices,emit,sock,configuration:()=>configuration,regrant:()=>{epoch='second';allowed.add(g);}};
}

test('real adapter scheduling processes all 600 authorized history entries and explicitly reports oversized batches',async()=>{
  const f=adapterFixture();await f.connector.start();try{
    assert.equal(f.configuration().syncFullHistory,false);assert.equal(f.configuration().shouldSyncHistoryMessage({}),true);
    f.emit('messaging-history.set',{messages:Array.from({length:600},(_,i)=>raw('m'+i)),peerDataRequestSessionId:'one'});
    await f.connector.ingestion!.settled();assert.equal(f.received.length,600);
    f.emit('messaging-history.set',{messages:Array.from({length:3000},(_,i)=>raw('over'+i))});await f.connector.ingestion!.settled();
    assert.equal(f.received.length,600+2048);assert.ok(f.notices.some(n=>n.phase==='overload'&&n.received===3000&&n.final));
    assert.ok(f.notices.every(n=>!JSON.stringify(n).includes('synthetic history')));
  }finally{await f.connector.stop();}
});

test('queue filters unapproved groups before normalization and invalidates old permissions after revoke/regrant',async()=>{
  const f=adapterFixture();let release!:()=>void;await f.connector.start();try{
    let touched=false;const unapproved:any={key:{remoteJid:other,id:'private'},get message(){touched=true;throw new Error('private');}};
    f.emit('messaging-history.set',{messages:[unapproved]});assert.equal(touched,false);assert.equal(f.notices.length,0);
    const gate=new Promise<void>(r=>release=r);f.h.incoming=async m=>{await gate;if(m.guard!())f.received.push(m);};
    f.emit('messaging-history.set',{messages:[raw('queued1'),raw('queued2')]});f.allowed.delete(g);f.regrant();release();await f.connector.ingestion!.settled();
    assert.equal(f.received.length,0);
    f.emit('messaging-history.set',{messages:[raw('fresh')]});await f.connector.ingestion!.settled();assert.equal(f.received[0].id,'fresh');
  }finally{release?.();await f.connector.stop();}
});

test('anchor validation never invents keys and provider return value remains only a request ID',async()=>{
  const f=adapterFixture();await f.connector.start();try{
    const good=message('good');assert.ok(f.connector.historyAnchor(g,good));assert.ok(f.connector.historyAnchor(g,message('outgoing',g,Date.now()-10000,true)));
    for(const patch of [{raw:undefined},{raw:'invalid'},{deleted:true},{kind:'unsupported'},{status:'pending'},{status:'failed'},{raw:JSON.stringify(raw('good',other))},{raw:JSON.stringify({...raw('good'),key:{id:'good',remoteJid:g}})}])
      assert.equal(f.connector.historyAnchor(g,{...good,...patch} as StoredMessage),false);
    let arguments_:any[]=[];f.sock.fetchMessageHistory=async(...args:any[])=>{arguments_=args;return 'submitted-only';};
    assert.equal(await f.connector.history(g,good),'submitted-only');assert.deepEqual(arguments_,[40,raw('good',g,good.time).key,Math.floor(good.time/1000)]);
  }finally{await f.connector.stop();}
});

test('byte limits and synchronous consumer errors are reported without storing payloads',async()=>{
  const notices:HistoryNotice[]=[];let consumed=0;
  const queue=new IngestionQueue(()=>{consumed++;throw new Error('private error');},()=>true,()=>true,n=>notices.push(n));
  queue.submit([{...message('oversize'),raw:'x'.repeat(300000)}],true);await queue.settled();assert.equal(consumed,0);assert.ok(notices.some(n=>n.phase==='overload'));
  queue.submit([message('throws')],true);await queue.settled();assert.equal(consumed,1);assert.ok(notices.some(n=>n.phase==='error'));assert.ok(!JSON.stringify(notices).includes('private error'));
});

test('stalled consumers report failure, invalidate their work and hold bounded capacity until unwound',async t=>{
  t.mock.timers.enable({apis:['setTimeout']});let release!:()=>void,started!:()=>void,calls=0;const notices:HistoryNotice[]=[];
  const waiting=new Promise<void>(r=>started=r),gate=new Promise<void>(r=>release=r);let held:Incoming|undefined;
  const queue=new IngestionQueue(async m=>{calls++;held=m;started();await gate;},()=>true,()=>true,n=>notices.push(n),{messages:2,bytes:2048,batches:1});
  queue.submit([message('stalled')],true);await waiting;t.mock.timers.tick(30001);await new Promise<void>(r=>setImmediate(r));
  assert.ok(notices.some(n=>n.phase==='error'));assert.equal(held!.guard!(),false);
  queue.submit([message('extra1'),message('extra2')],true);assert.equal(calls,1);assert.ok(notices.some(n=>n.phase==='overload'));
  release();await queue.settled();
});

test('257 simultaneously receiving groups degrade to overload without throwing and expired slots are reclaimed',async()=>{
  const f=await fixture();try{
    let now=Date.now();const tracker=new HistoryTracker(f.store,()=>now);
    for(let i=0;i<256;i++)tracker.notice(f.a.familyId,{groupId:'tracked-'+i,phase:'start',received:1,processed:0,history:true});
    assert.doesNotThrow(()=>tracker.notice(f.a.familyId,{groupId:'extra',phase:'overload',received:1,processed:0,history:true}));
    let view=tracker.view(f.a.familyId,g);assert.equal(view.phase,'overloaded');assert.equal(view.reason,'capacity');assert.equal(view.requestable,false);
    now+=45001;view=tracker.view(f.a.familyId,g);assert.equal(view.phase,'partial');assert.equal(view.reason,null);
  }finally{await f.close();}
});

test('admitted processing cannot time out or enable a duplicate request while its batch is draining',async()=>{
  const f=await fixture();try{
    let now=Date.now();const tracker=new HistoryTracker(f.store,()=>now),request=tracker.begin(f.a.familyId,g)!;
    request.state.providerId='match';request.state.phase='waiting';request.state.deadline=now+45000;
    tracker.notice(f.a.familyId,{groupId:g,phase:'start',history:true,requestId:'match',received:2,processed:0,batchId:'batch',admitted:2});
    now+=60000;let view=tracker.view(f.a.familyId,g);assert.equal(view.phase,'receiving');assert.equal(view.processing,true);assert.equal(view.requestable,false);
    tracker.notice(f.a.familyId,{groupId:g,phase:'end',history:true,requestId:'match',received:2,processed:2,batchId:'batch',admitted:2,final:true,finished:true});
    view=tracker.view(f.a.familyId,g);assert.equal(view.phase,'partial');assert.equal(view.processing,false);assert.equal(view.requestable,true);
  }finally{await f.close();}
});

test('reconnecting disposes old queued buffers and shares a bound on unresolved consumers across generations',async()=>{
  const f=adapterFixture();let release!:()=>void,started!:()=>void,calls=0;const waiting=new Promise<void>(r=>started=r),gate=new Promise<void>(r=>release=r);
  f.h.incoming=async m=>{calls++;started();await gate;if(m.guard!())f.received.push(m);};
  await f.connector.start();try{
    f.emit('messaging-history.set',{messages:Array.from({length:600},(_,i)=>raw('old-'+i))});await waiting;const old=f.connector.ingestion!;
    for(let i=0;i<3;i++){await f.connector.connect();f.emit('messaging-history.set',{messages:[raw('new-'+i)]});await f.connector.ingestion!.settled();}
    await old.settled();assert.equal(calls,1);assert.ok(f.notices.some(n=>n.phase==='overload'));
    release();await new Promise<void>(r=>setImmediate(r));f.emit('messaging-history.set',{messages:[raw('fresh-after-unwind')]});await f.connector.ingestion!.settled();
    assert.deepEqual(f.received.map(m=>m.id),['fresh-after-unwind']);
  }finally{release?.();await f.connector.stop();}
});

test('disposing active ingestion retires processing markers and reconnect clears obsolete status',async()=>{
  const f=await fixture();let release!:()=>void,started!:()=>void;
  try{
    f.seed(message('anchor'));
    const waiting=new Promise<void>(r=>started=r),gate=new Promise<void>(r=>release=r);
    const queue=new IngestionQueue(async()=>{started();await gate;},()=>true,()=>true,n=>f.c.hooks.history!(n));
    queue.submit([message('cancelled')],true);await waiting;
    assert.equal((await f.req()).json().processing,true);
    queue.dispose();queue.dispose();
    let status=(await f.req()).json();assert.equal(status.processing,false);assert.equal(status.phase,'failed');assert.equal(status.incomplete,true);
    f.c.hooks.state('reconnecting');f.c.hooks.state('connected');
    status=(await f.req()).json();assert.equal(status.processing,false);assert.equal(status.phase,'partial');assert.equal(status.requestable,true);
    release();await new Promise<void>(r=>setImmediate(r));
    assert.equal((await f.req()).json().totalStored,1);
  }finally{release?.();await f.close();}
});

test('underlying request slots belong to the connector and late old completions cannot free new slots',async t=>{
  const f=await fixture();t.mock.timers.enable({apis:['Date','setTimeout'],now:Date.now()});const oldFinish:((id:string)=>void)[]=[],newFinish:((id:string)=>void)[]=[];
  try{
    f.seed(message('anchor'));
    const fill=async(c:HistoryMock,finish:((id:string)=>void)[])=>{
      for(let i=0;i<2;i++){let started!:()=>void;const waiting=new Promise<void>(r=>started=r);c.result=()=>{started();return new Promise<string>(r=>finish.push(r));};
        const pending=f.req(g,'POST',{});await waiting;t.mock.timers.tick(10001);assert.equal((await pending).json().phase,'timeout');t.mock.timers.tick(15001);}
    };
    await fill(f.c,oldFinish);assert.equal((await f.req(g,'POST',{})).statusCode,429);
    await f.service.disconnect(f.a.familyId,false);await f.service.connect(f.a.familyId);const replacement=f.service.connector(f.a.familyId)as HistoryMock;
    await fill(replacement,newFinish);for(const resolve of oldFinish)resolve('old');await new Promise<void>(r=>setImmediate(r));assert.equal((await f.req(g,'POST',{})).statusCode,429);
  }finally{for(const resolve of [...oldFinish,...newFinish])resolve('cleanup');await f.close();}
});

test('a replacement connector shares the family ingestion bound while a disposed transport is still hung',async()=>{
  const budget={active:0},a=adapterFixture(budget),b=adapterFixture(budget);let release!:()=>void,started!:()=>void;
  const waiting=new Promise<void>(r=>started=r),gate=new Promise<void>(r=>release=r);a.h.incoming=async()=>{started();await gate;};
  await a.connector.start();try{
    a.emit('messaging-history.set',{messages:[raw('old')]});await waiting;await a.connector.stop();await b.connector.start();
    b.emit('messaging-history.set',{messages:[raw('replacement')]});await b.connector.ingestion!.settled();assert.equal(b.received.length,0);assert.ok(b.notices.some(n=>n.phase==='overload'));
    release();await new Promise<void>(r=>setImmediate(r));b.emit('messaging-history.set',{messages:[raw('after-release')]});await b.connector.ingestion!.settled();assert.equal(b.received[0].id,'after-release');
  }finally{release?.();await a.connector.stop();await b.connector.stop();}
});

test('history requests share a family rate bound even across different groups and empty replies',async()=>{
  const f=await fixture();try{
    for(let i=0;i<7;i++){
      const group='90000'+i+'@g.us';f.store.syncGroups(f.a.familyId,[{id:group,title:'Synthetic rate '+i}]);await f.service.grant(f.a.familyId,f.a.childId,group,true);f.seed(message('anchor-'+i,group));
      const result=await f.req(group,'POST',{});assert.equal(result.statusCode,i<6?200:429);if(i<6)f.c.hooks.historyEmpty!('request-one');
    }
    assert.equal(f.c.calls.length,6);
  }finally{await f.close();}
});

test('provider discovery precedes private message admission and ignores broadcasts, stale events and contacts without chats',async()=>{
 const f=adapterFixture(); const discovered:any[]=[];
 f.h.discoverDirect=chats=>{discovered.push(...chats);for(const c of chats)f.allowed.add(c.id);};
 await f.connector.start();try{
  const direct='15550001111@s.whatsapp.net', lid='123456789@lid';
  f.emit('contacts.upsert',[{id:direct,name:'Contact only'}]);assert.equal(discovered.length,0);
  f.emit('messaging-history.set',{chats:[{id:direct,name:'Synthetic direct'}],messages:[raw('direct-old',direct)]});
  await f.connector.ingestion!.settled();
  assert.equal(f.received[0].groupId,direct);assert.equal(f.received[0].history!==undefined,true);
  f.emit('messages.upsert',{type:'notify',messages:[raw('lid-live',lid),raw('status','status@broadcast')]});
  await f.connector.ingestion!.settled();
  assert.deepEqual(f.received.map(m=>m.id),['direct-old','lid-live']);
  f.emit('contacts.upsert',[{id:direct,name:'Updated name'}]);assert.equal(discovered.at(-1).title,'Updated name');
  const before=discovered.length;await f.connector.stop();f.emit('chats.upsert',[{id:'999@s.whatsapp.net',name:'Late'}]);
  assert.equal(discovered.length,before);
 }finally{await f.connector.stop();}
});

test('initial history waits for verified group membership instead of discarding catalog-race messages',async()=>{
 const f=adapterFixture(); f.allowed.delete(g);let finish!:(v:any)=>void;
 f.h.discoverGroups=groups=>{for(const x of groups)f.allowed.add(x.id);};
 f.sock.groupFetchAllParticipating=()=>new Promise(r=>finish=r);
 await f.connector.start();try{
  f.emit('messaging-history.set',{messages:[raw('initial'),raw('departed',other)]});
  assert.equal(f.received.length,0);
  finish({[g]:{id:g,subject:'Verified member'}});
  await new Promise<void>(r=>setImmediate(r));await f.connector.ingestion!.settled();
  assert.deepEqual(f.received.map(m=>m.id),['initial']);
 }finally{await f.connector.stop();}
});

test('late verified group fetch cannot reintroduce chats after connector stop',async()=>{
 const f=adapterFixture();f.allowed.delete(g);let finish!:(v:any)=>void;let calls=0;
 f.h.discoverGroups=()=>{calls++;};f.sock.groupFetchAllParticipating=()=>new Promise(r=>finish=r);
 await f.connector.start();
 f.emit('messaging-history.set',{messages:[raw('initial')]});await f.connector.stop();
 finish({[g]:{id:g,subject:'Late'}});
 await new Promise<void>(r=>setImmediate(r));
 assert.equal(calls,0);assert.equal(f.received.length,0);
});

test('private original keys work end to end for send, read, media normalization and history requests',async()=>{
 const f=adapterFixture(),direct='15550002222@s.whatsapp.net';f.allowed.add(direct);
 const sent:any[]=[],reads:any[]=[];
 f.sock.sendMessage=async(jid:string,content:any,options:any)=>{sent.push({jid,content,options});return raw(options.messageId,jid,Date.now(),true);};
 f.sock.readMessages=async(keys:any)=>{reads.push(keys);};
 await f.connector.start();try{
  const outgoing=await f.connector.send({groupId:direct,id:'sent-direct',text:'Synthetic private text',kind:'text'});
  assert.equal(outgoing.groupId,direct);
  const old=message('old-direct',direct);
  assert.equal(f.connector.historyAnchor(direct,old),true);
  assert.equal(await f.connector.history(direct,old),'provider-request');
  await f.connector.read(direct,[old]);assert.equal(reads[0][0].remoteJid,direct);
  assert.equal(sent[0].jid,direct);
  f.emit('messages.upsert',{type:'notify',messages:[{...raw('media-direct',direct),message:{imageMessage:{caption:'Synthetic caption',mimetype:'image/jpeg'}}}]});
  await f.connector.ingestion!.settled();
  assert.equal(f.received.at(-1)?.kind,'image');
  assert.equal(f.received.at(-1)?.groupId,direct);
 }finally{await f.connector.stop();}
});

test('initial history rechecks stale known group membership before retaining messages',async()=>{
 const f=adapterFixture();let finish!:(v:any)=>void;
 f.h.discoverGroups=groups=>{f.allowed.clear();for(const x of groups)f.allowed.add(x.id);};
 f.sock.groupFetchAllParticipating=()=>new Promise(r=>finish=r);
 await f.connector.start();try{
  f.emit('messaging-history.set',{messages:[raw('stale-known')]});
  assert.equal(f.received.length,0);finish({});
  await new Promise<void>(r=>setImmediate(r));await f.connector.ingestion!.settled();
  assert.equal(f.received.length,0);
 }finally{await f.connector.stop();}
});

test('live ingress also waits initial membership and stale fetches cannot return an empty authoritative catalog',async()=>{
 const f=adapterFixture();let finish!:(v:any)=>void;
 f.h.discoverGroups=groups=>{f.allowed.clear();for(const x of groups)f.allowed.add(x.id);};
 f.sock.groupFetchAllParticipating=()=>new Promise(r=>finish=r);
 await f.connector.start();
 f.emit('messages.upsert',{type:'notify',messages:[raw('stale-live')]});assert.equal(f.received.length,0);
 finish({});await new Promise<void>(r=>setImmediate(r));assert.equal(f.received.length,0);
 const pending=f.connector.groups();await f.connector.stop();finish({});
 await assert.rejects(pending,/Stale conversation catalog/);
});

test('only explicit Baileys notify arrivals are live; append/replay rows stay history and requestId duplicates stay suppressed',async()=>{
 const f=adapterFixture();await f.connector.start();try{
  f.emit('messages.upsert',{type:'notify',messages:[raw('live-notify')]});
  f.emit('messages.upsert',{type:'append',messages:[raw('offline-append')]});
  f.emit('messages.upsert',{messages:[raw('unknown-type')]});
  f.emit('messages.upsert',{type:'notify',requestId:'phone-response',messages:[raw('duplicate-response')]});
  await f.connector.ingestion!.settled();
  assert.deepEqual(f.received.map(m=>[m.id,!!m.history]),[
   ['live-notify',false],['offline-append',true],['unknown-type',true],
  ]);
 }finally{await f.connector.stop();}
});
