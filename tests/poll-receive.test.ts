import {test} from 'node:test';
import assert from 'node:assert/strict';
import {randomBytes,randomUUID} from 'node:crypto';
import {readFileSync} from 'node:fs';
import React from 'react';
import {renderToStaticMarkup} from 'react-dom/server';
import {Store} from '../src/store.js';
import {ChatService} from '../src/service.js';
import {open,seal} from '../src/crypto.js';
import {normalizeIncoming} from '../src/connectors/baileys.js';
import {pollActorLimit,applyPollVote,newPollState,observedPollResults} from '../src/polls/receive.js';
import {acceptPollVote} from '../src/polls/pending.js';
import {identities} from '../src/polls/normalize.js';
import {PollMessage} from '../web/PollMessage';
import {pollResultDisplay,observedResultsNotice,pollUnavailable} from '../src/poll-display.js';
import {IngestionQueue} from '../src/connectors/ingestion.js';
import {fixture,group,other,raw} from './helpers/message-fixture.js';
import {poll,vote,secret,creator,voter,choices} from './helpers/poll-fixture.js';
const result=(f:any,id='poll')=>f.service.public(f.a.familyId,f.get(id)!).pollResults;
const counts=(f:any,id='poll')=>result(f,id)?.counts;

test('official helper receives one vote with minimal encrypted context and no secret, identity or ciphertext in DTO',async()=>{
 const f=await fixture();try{
  await f.ingest(poll());assert.equal(result(f),undefined);await f.emit('messages.upsert',{type:'notify',messages:[vote('one',['First'])]});
  assert.deepEqual(result(f),{counts:[1,0],partial:true});assert.equal(f.get('one'),undefined);
  const m=f.get('poll')!;assert.equal(m.raw,undefined);assert.equal(m.pollState!.secret,secret.toString('base64'));assert.equal(m.pollState!.ballots.length,1);
  const response=await f.request('/messages');assert.equal(response.statusCode,200);
  for(const value of ['pollState','pollContext','creators','voters',secret.toString('base64'),creator,voter])assert.ok(!response.body.includes(value));
  const row=f.store.one('SELECT body FROM messages WHERE family_id=? AND group_id=? AND id=?',f.a.familyId,group,'poll');
  assert.ok(!Buffer.from(row.body).includes(Buffer.from(secret.toString('base64'))));assert.ok(!readFileSync(f.store.config.dataDir+'/app.sqlite').includes(Buffer.from(creator)));
 }finally{await f.close();}
});

test('early encrypted votes survive Store reload, original history arrival and persistent withdrawal tombstones',async()=>{
 const f=await fixture();let reopened:Store|undefined;try{
  const time=Date.now()-10000;
  await f.ingest(vote('selected',['First'],time));await f.ingest(vote('withdrawn',[],time+200));await f.ingest(vote('stale',['Second'],time+100));
  const row=f.store.one('SELECT body,slot FROM pending_poll_votes WHERE family_id=?',f.a.familyId);assert.ok(row);assert.ok(!Buffer.from(row.body).includes(Buffer.from(voter)));assert.ok(!row.slot.includes(voter));
  reopened=new Store(f.store.config);const service=new ChatService(reopened);
  await service.ingest(f.a.familyId,{...normalizeIncoming(poll())!,history:{}});
  assert.deepEqual(counts(f),[0,0]);assert.equal(f.get('poll')!.pollState!.ballots.length,1);assert.deepEqual(f.get('poll')!.pollState!.ballots[0].selected,[]);
  assert.equal(f.store.one('SELECT count(*) AS n FROM pending_poll_votes').n,0);
  await f.ingest(vote('older',['First'],time));assert.deepEqual(counts(f),[0,0]);
  await f.ingest(vote('changed',['Second'],time+300));assert.deepEqual(counts(f),[0,1]);
  await f.ingest(poll());assert.deepEqual(counts(f),[0,1]);
 }finally{reopened?.close();await f.close();}
});

test('only provider-observed PN/LID evidence is tried, authenticated aliases bridge split histories without double counting',async()=>{
 const f=await fixture();try{
  const time=Date.now()-10000;
  await f.ingest(poll('poll',{}, {participantAlt:'111@s.whatsapp.net'}));
  await f.ingest(vote('lid',['First'],time));
  await f.ingest(vote('pn',['Second'],time+100,{creator:'111@s.whatsapp.net',voter:'222@s.whatsapp.net'}));assert.deepEqual(counts(f),[1,1]);
  // An authenticated provider key now binds these aliases; a stale bridge must
  // keep the newest selection while collapsing the duplicate identity rows.
  await f.ingest(vote('bridge',['First'],time+50,{voterAlt:'222@s.whatsapp.net'}));assert.deepEqual(counts(f),[0,1]);
  assert.equal(f.get('poll')!.pollState!.ballots.length,1);
  await f.ingest(vote('remove',[],time+200,{voter:'222@s.whatsapp.net'}));assert.deepEqual(counts(f),[0,0]);
  await f.ingest(vote('wrong',['First'],time+300,{creator:'999@lid'}));assert.deepEqual(counts(f),[0,0]);
  assert.deepEqual(identities(['101:77@lid','111:2@s.whatsapp.net']),['101@lid','111@s.whatsapp.net']);assert.deepEqual(identities(['https://invalid.example','101@g.us']),[]);
 }finally{await f.close();}
});

test('own polls and votes use actual supplied self identities, not invented fromMe aliases',async()=>{
 const f=await fixture();try{
  const self={id:'111:2@s.whatsapp.net',lid:'101:77@lid'};
  const own=poll('poll',{}, {fromMe:true,participant:undefined});
  assert.equal(normalizeIncoming(own)!.pollContext,undefined);
  await f.service.ingest(f.a.familyId,normalizeIncoming(own,self)!);
  const ownVote=vote('own',['First'],Date.now()-1000,{voter:creator,keyExtra:{fromMe:true,participant:undefined}});
  await f.service.ingest(f.a.familyId,normalizeIncoming(ownVote,self)!);assert.deepEqual(counts(f),[1,0]);
 }finally{await f.close();}
});

test('unverifiable/duplicate/oversized selections, tampering and missing origins never fabricate counts',async()=>{
 const f=await fixture();try{
  const time=Date.now()-10000;await f.ingest(poll());
  for(const [id,selected,options]of [['unknown',['UNKNOWN'],{}],['too-many',choices,{}],['duplicate',['First','First'],{}],['wrong-key',['First'],{key:Buffer.alloc(32,8)}]]as any[])
   await f.ingest(vote(id,selected,time,options));
  assert.equal(counts(f),undefined);
  const corrupt=vote('tampered',['First']);corrupt.message!.pollUpdateMessage!.vote!.encPayload![0]^=1;await f.ingest(corrupt);assert.equal(counts(f),undefined);
  const missing=poll('missing');delete missing.message!.messageContextInfo;await f.ingest(missing);await f.ingest(vote('no-key',['First'],time,{target:'missing'}));assert.equal(counts(f,'missing'),undefined);
  // A fresh authenticated redelivery can supply the previously missing key.
  await f.ingest(poll('missing'));assert.deepEqual(counts(f,'missing'),[1,0]);
  await f.ingest(poll('duplicate-options',{options:[{optionName:'First'},{optionName:'First'}]}));assert.equal(f.get('duplicate-options')!.pollState,undefined);
  await f.ingest(poll('multi',{selectableOptionsCount:2}));await f.ingest(vote('multi-vote',choices,time,{target:'multi'}));assert.deepEqual(counts(f,'multi'),[1,1]);
 }finally{await f.close();}
});

test('temporary votes/origins, edits, early deletes and replay cannot retain or resurrect poll keys or results',async()=>{
 const f=await fixture();try{
  const privatePoll=poll('private');privatePoll.message={viewOnceMessageV2:{message:privatePoll.message}};
  await f.ingest(vote('before',['First'],Date.now()-1000,{target:'private'}));await f.ingest(privatePoll);
  assert.equal(f.get('private')!.pollState,undefined);assert.equal(f.store.one('SELECT count(*) AS n FROM pending_poll_votes').n,0);await f.ingest(poll('private'));assert.equal(f.get('private')!.restricted,true);
  await f.ingest(poll());const privateVote=vote('private-vote',['First']);privateVote.message={ephemeralMessage:{message:privateVote.message}};await f.ingest(privateVote);assert.equal(counts(f),undefined);
  await f.ingest(vote('okay',['First']));assert.deepEqual(counts(f),[1,0]);
  const change=poll();await f.ingest(raw('edit',{editedMessage:{message:change.message}})); // unrelated edit cannot copy state
  await f.service.ingest(f.a.familyId,{...normalizeIncoming(change)!,edited:true,editTime:Date.now()});assert.equal(f.get('poll')!.pollState,undefined);assert.equal(counts(f),undefined);
  await f.ingest(poll());assert.equal(f.get('poll')!.pollState,undefined); // old source cannot undo an edit
  f.service.update(f.a.familyId,group,'early',{deleted:true});await f.ingest(vote('deleted-vote',['First'],Date.now()-1000,{target:'early'}));await f.ingest(poll('early'));
  assert.equal(f.get('early')!.deleted,true);assert.equal(f.get('early')!.pollState,undefined);
 }finally{await f.close();}
});

test('poll reception obeys family/conversation/child access and generation retention guards',async()=>{
 const f=await fixture({child:true});try{
  await f.ingest(poll());await f.ingest(vote('vote',['First']));
  const child=f.store.createSession(f.store.user(f.store.one('SELECT * FROM users WHERE id=?',f.a.childId)));
  const read=()=>f.app.inject({url:'/api/groups/'+encodeURIComponent(group)+'/messages',headers:{cookie:'sid='+child.sid}});
  assert.equal((await read()).statusCode,404);await f.service.grant(f.a.familyId,f.a.childId,group,true);assert.match((await read()).body,/pollResults/);await f.service.grant(f.a.familyId,f.a.childId,group,false);assert.equal((await read()).statusCode,404);
  const b=await f.store.createFamily('Other',{name:'Other',password:randomBytes(24).toString('hex')});f.store.syncGroups(b.familyId,[{id:group,title:'Other'}]);
  assert.equal(f.store.getMessage(b.familyId,group,'poll'),undefined);
  const cross=vote('cross',['Second']);cross.message!.pollUpdateMessage!.pollCreationMessageKey!.remoteJid=other;assert.equal(normalizeIncoming(cross)!.pollVote,undefined);await f.ingest(cross);assert.deepEqual(counts(f),[1,0]);
  const foreign=vote('other',['First']);foreign.key.remoteJid=other;foreign.message!.pollUpdateMessage!.pollCreationMessageKey!.remoteJid=other;await f.ingest(foreign);assert.deepEqual(counts(f),[1,0]);
  const normalized=normalizeIncoming(vote('inactive',['Second']))!;await f.service.ingest(f.a.familyId,normalized,()=>false);assert.deepEqual(counts(f),[1,0]);
  f.store.purgeGroup(f.a.familyId,other);assert.equal(f.store.one('SELECT count(*) AS n FROM pending_poll_votes WHERE group_id=?',other).n,0);
 }finally{await f.close();}
});

test('pending encrypted work has group/family/TTL limits; ballot capacity preserves existing withdrawal tombstones',async()=>{
 const f=await fixture();try{
  const base=normalizeIncoming(vote('pending',['First']))!.pollVote!;
  for(let i=0;i<270;i++)acceptPollVote(f.store,f.a.familyId,group,{...base,id:'pending-'+i,target:'target-'+i});
  assert.equal(f.store.one('SELECT count(*) AS n FROM pending_poll_votes').n,256);
  f.store.run('UPDATE pending_poll_votes SET received=0');await f.ingest(poll('target-269'));assert.equal(counts(f,'target-269'),undefined);
  const normalized=normalizeIncoming(poll())!,m:any={...normalized,status:'sent',reactions:{},pollState:newPollState(normalized.pollContext,normalized.text,normalized.poll!.options)};
  m.pollState.ballots=Array.from({length:pollActorLimit},(_,i)=>({aliases:[`${1000+i}@lid`],id:'withdrawal-'+i,time:100,selected:[]}));
  const event=normalizeIncoming(vote('overflow',['First']))!.pollVote!;
  assert.equal(applyPollVote(m,event),true);assert.equal(m.pollState.ballots.length,pollActorLimit);assert.equal(m.pollState.limited,true);assert.deepEqual(observedPollResults(m)!.counts,[0,0]);
  const old=normalizeIncoming(vote('old',['First'],50,{voter:'1000@lid'}))!.pollVote!;assert.equal(applyPollVote(m,old),false);assert.deepEqual(observedPollResults(m)!.counts,[0,0]);
 }finally{await f.close();}
});

test('invalid crypto shapes and malformed legacy state fail closed; poll result DOM is bounded and explicitly partial',()=>{
 const base=vote('bad',['First']);
 for(const patch of [{encIv:Buffer.alloc(13)},{encPayload:Buffer.alloc(8193)},{encPayload:Buffer.alloc(15)}]){
  const m=structuredClone(base);Object.assign(m.message!.pollUpdateMessage!.vote!,patch);assert.equal(normalizeIncoming(m)!.pollVote,undefined);
 }
 const normalized=normalizeIncoming(poll())!,m:any={...normalized,status:'sent',reactions:{},pollState:newPollState(normalized.pollContext,normalized.text,normalized.poll!.options)};
 for(const state of [{...m.pollState,ballots:Array(2)},{...m.pollState,ballots:[{aliases:[],time:1,id:'x',selected:[0]}]},{...m.pollState,secret:'INVALID'}])assert.equal(observedPollResults({...m,pollState:state}),undefined);
 const html=renderToStaticMarkup(React.createElement(PollMessage,{question:'<script>bad</script>',options:choices,results:{counts:[1,0],partial:true,secret:'HIDDEN'}}));
 assert.ok(html.includes(observedResultsNotice));assert.match(html,/&lt;script&gt;/);assert.ok(!html.includes('HIDDEN'));assert.ok(!/<(?:button|input|script|a)\b/.test(html));
 assert.equal(pollResultDisplay({counts:Array(2),partial:true},2),undefined);assert.equal(pollResultDisplay({counts:[513,0],partial:true},2),undefined);
 const unavailable=renderToStaticMarkup(React.createElement(PollMessage,{question:'Question',options:choices,results:{counts:[1,0],partial:false}}));assert.ok(unavailable.includes(pollUnavailable));
});

test('missing-key origin stays pinned; another creator and an unbound legacy row cannot adopt a new key',async()=>{
 const f=await fixture();try{
  const first=poll();delete first.message!.messageContextInfo;await f.ingest(first);
  assert.deepEqual(f.get('poll')!.pollState!.creators,[creator]);assert.equal(f.get('poll')!.pollState!.secret,undefined);
  await f.ingest(vote('legitimate',['First']));
  await f.ingest(poll('poll',{}, {participant:'999@lid'}));
  await f.ingest(vote('forged',['Second'],Date.now()-500,{creator:'999@lid'}));
  assert.deepEqual(f.get('poll')!.pollState!.creators,[creator]);assert.equal(f.get('poll')!.pollState!.secret,undefined);assert.equal(counts(f),undefined);
  await f.ingest(poll());assert.deepEqual(counts(f),[1,0]);
  const old=normalizeIncoming(poll('legacy'))!;delete old.pollContext;await f.service.ingest(f.a.familyId,old);
  await f.ingest(poll('legacy'));await f.ingest(vote('legacy-vote',['First'],Date.now()-1000,{target:'legacy'}));assert.equal(f.get('legacy')!.pollState,undefined);assert.equal(counts(f,'legacy'),undefined);
  await f.service.ingest(f.a.familyId,{...normalizeIncoming(poll('placeholder'))!,kind:'unsupported',pollContext:undefined,text:'Old generic poll'});
  await f.ingest(poll('placeholder'));assert.equal(f.get('placeholder')!.kind,'poll');assert.equal(f.get('placeholder')!.pollState,undefined);
 }finally{await f.close();}
});

test('unresolved early votes survive origin/duplicate delivery until matching creator alias enrichment; duplicates cause no writes or notifications',async()=>{
 const f=await fixture();try{
  await f.ingest(vote('early-alias',['First'],Date.now()-1000,{creator:'111@s.whatsapp.net'}));
  await f.ingest(poll());assert.equal(counts(f),undefined);assert.equal(f.store.one('SELECT count(*) AS n FROM pending_poll_votes').n,1);
  const changes=f.store.one('SELECT total_changes() AS n').n;let notices=0;
  const listener=()=>notices++;f.service.listeners.add(listener);
  for(let i=0;i<10;i++)await f.ingest(poll());
  await f.ingest(poll('poll',{}, {participant:'999@lid'}));
  assert.equal(f.store.one('SELECT total_changes() AS n').n,changes);assert.equal(notices,0);f.service.listeners.delete(listener);
  assert.equal(f.store.one('SELECT count(*) AS n FROM pending_poll_votes').n,1);
  await f.ingest(poll('poll',{}, {participantAlt:'111@s.whatsapp.net'}));
  assert.deepEqual(counts(f),[1,0]);assert.equal(f.store.one('SELECT count(*) AS n FROM pending_poll_votes').n,0);
  assert.deepEqual(f.get('poll')!.pollState!.creators,[creator,'111@s.whatsapp.net']);
 }finally{await f.close();}
});

test('rollback-era DTO cannot see secrets and old-style encrypted body rewrites/inserts erase private state atomically',async()=>{
 const f=await fixture();try{
  await f.ingest(poll());await f.ingest(vote('okay',['First']));await f.ingest(vote('unknown-context',['First'],Date.now()-1000,{creator:'999@lid'}));
  const key=f.store.groupKey(f.a.familyId,group),aad=`${f.a.familyId}:${group}:poll`;
  const messageRow=()=>f.store.one('SELECT body FROM messages WHERE family_id=? AND group_id=? AND id=?',f.a.familyId,group,'poll');
  const legacy=JSON.parse(open(key,messageRow().body,aad).toString());assert.equal(legacy.pollState,undefined);assert.ok(!JSON.stringify(legacy).includes(secret.toString('base64')));
  f.service.update(f.a.familyId,group,'poll',{status:'read'});assert.deepEqual(counts(f),[1,0]);assert.equal(f.store.one('SELECT count(*) AS n FROM pending_poll_votes').n,1);
  // A current-code failure after rewriting message JSON must roll back both
  // private tables as well, rather than leaving half-erased context.
  const current=f.get('poll')!,bad:any={...current,pollState:{...current.pollState}};bad.pollState.circular=bad.pollState;
  assert.throws(()=>f.store.putMessage(f.a.familyId,bad));assert.deepEqual(counts(f),[1,0]);assert.equal(f.store.one('SELECT count(*) AS n FROM pending_poll_votes').n,1);
  const deleted={...legacy,deleted:true,text:''};
  f.store.run('UPDATE messages SET body=? WHERE family_id=? AND group_id=? AND id=?',seal(key,JSON.stringify(deleted),aad),f.a.familyId,group,'poll');
  assert.equal(f.store.one('SELECT count(*) AS n FROM poll_contexts').n,0);assert.equal(f.store.one('SELECT count(*) AS n FROM pending_poll_votes').n,0);
  await f.ingest(vote('before-delete',['First'],Date.now()-1000,{target:'early-delete'}));
  const stub={id:'early-delete',groupId:group,sender:'',fromMe:false,time:Date.now(),kind:'text',text:'',deleted:true,status:'unknown',reactions:{}};
  f.store.run('INSERT INTO messages VALUES(?,?,?,?,?)',f.a.familyId,group,stub.id,stub.time,seal(key,JSON.stringify(stub),`${f.a.familyId}:${group}:${stub.id}`));
  assert.equal(f.store.one('SELECT count(*) AS n FROM pending_poll_votes').n,0);
 }finally{await f.close();}
});

test('equal timestamps use deterministic event IDs; disabled vote boundary never calls provider transport',async()=>{
 const f=await fixture();try{
  const time=Date.now()-1000;await f.ingest(poll());
  await f.ingest(vote('z-new',['Second'],time));await f.ingest(vote('a-old',['First'],time));assert.deepEqual(counts(f),[0,1]);
  assert.equal(f.connector.pollVoting.available,false);
  for(const optionIndexes of [[],[0],[0,1],[-1],[99]])await assert.rejects(f.connector.pollVoting.send({conversationId:group,pollId:'poll',optionIndexes}),/independent interoperability verification/);
  await assert.rejects(f.connector.send({id:'raw',groupId:group,kind:'pollVote',text:'',bytes:Buffer.from('x')}as any),/Message sending unavailable/);
  assert.equal(f.sent.length,0);assert.equal((await f.request('/polls','POST',{pollId:'poll',optionIndexes:[0],clientId:randomUUID()})).statusCode,404);
 }finally{await f.close();}
});

test('ingestion byte budget includes encrypted vote envelopes and private origin metadata',async()=>{
 let consumed=0;const notices:any[]=[];
 const queue=new IngestionQueue(async()=>{consumed++;},()=>true,()=>true,n=>notices.push(n),{messages:10,bytes:1000,batches:8});
 const normalized=normalizeIncoming(vote('large',['First']))!;
 normalized.pollVote!.payload=Buffer.alloc(8192).toString('base64');queue.submit([normalized],true);await queue.settled();assert.equal(consumed,0);assert.ok(notices.some(n=>n.phase==='overload'));queue.dispose();
});

test('consuming pending votes and saving initial or enriched poll state is one transaction',async()=>{
 const f=await fixture();try{
  const original=f.store.putMessage.bind(f.store);let fail=false;
  f.store.putMessage=((family:string,message:any,live?:boolean)=>{if(fail&&message.kind==='poll')throw new Error('Synthetic write failure');return original(family,message,live);}) as any;
  await f.ingest(vote('before-first',['First']));fail=true;
  await assert.rejects(f.ingest(poll()),/Synthetic write failure/);
  assert.equal(f.get('poll'),undefined);assert.equal(f.store.one('SELECT count(*) AS n FROM pending_poll_votes').n,1);
  fail=false;await f.ingest(poll());assert.deepEqual(counts(f),[1,0]);assert.equal(f.store.one('SELECT count(*) AS n FROM pending_poll_votes').n,0);
  await f.ingest(vote('waiting-alias',['Second'],Date.now()-500,{creator:'111@s.whatsapp.net',voter:'303@lid'}));
  const enriched=poll('poll',{}, {participantAlt:'111@s.whatsapp.net'});fail=true;
  await assert.rejects(f.ingest(enriched),/Synthetic write failure/);
  assert.deepEqual(counts(f),[1,0]);assert.deepEqual(f.get('poll')!.pollState!.creators,[creator]);assert.equal(f.store.one('SELECT count(*) AS n FROM pending_poll_votes').n,1);
  fail=false;await f.ingest(enriched);assert.deepEqual(counts(f),[1,1]);assert.equal(f.store.one('SELECT count(*) AS n FROM pending_poll_votes').n,0);
 }finally{await f.close();}
});
