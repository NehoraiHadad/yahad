import {test} from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,writeFileSync,readFileSync,rmSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {randomBytes,randomUUID} from 'node:crypto';
import {createApp} from '../src/server.js';
import type {Config} from '../src/config.js';
async function fixture(){
  const dir=mkdtempSync(join(tmpdir(),'yachad-mode-'));
  const config:Config={dataDir:dir,masterKey:randomBytes(32),connector:'mock',realAllowed:false,host:'127.0.0.1',port:3000,origin:'http://localhost:3000',secureCookie:false,maxMedia:1048576,maxStorage:67108864,childIdleMs:60000,sessionMs:3600000,diagnosticsEnabled:false,updateStatusFile:join(dir,'root-status.json')};
  const {app,store}=await createApp(config,{boot:false});const password=randomBytes(24).toString('hex');
  const a=await store.createFamily('A',{username:'parent-a',password},{username:'child-a',password}),b=await store.createFamily('B',{username:'parent-b',password},{username:'child-b',password});
  const session=(id:string)=>store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?',id)));
  const parent=session(a.parentId),child=session(a.childId),other=session(b.parentId);
  const request=(s:typeof parent|null,method='GET',body?:any,csrf=true)=>app.inject({url:'/api/parent/updates',method:method as any,payload:body,headers:{origin:config.origin,...s?{cookie:'sid='+s.sid,...csrf?{'x-csrf-token':s.csrf}:{}}:{}}});
  let status:any={enabled:true,mode:'notify',modeControlAllowed:true,operator:{familyId:a.familyId,userId:a.parentId},phase:'available',currentVersion:'0.3.1',latestVersion:'0.3.2',checkedAt:Date.now(),updatedAt:Date.now(),error:null,rootSecret:'DO_NOT_EXPOSE'};
  const set=(patch:any={})=>{status={...status,...patch};writeFileSync(config.updateStatusFile!,JSON.stringify(status));};set();
  return{dir,request,parent,child,other,set,queue:()=>JSON.parse(readFileSync(join(dir,'update-request.json'),'utf8')),async close(){await app.close();rmSync(dir,{recursive:true,force:true});}};
}
test('mode control requires designated parent, session, CSRF and strict mode-only input',async()=>{
  const f=await fixture();try{
    const body={action:'set_mode',mode:'auto'};
    assert.equal((await f.request(null,'POST',body)).statusCode,401);
    assert.equal((await f.request(f.child,'POST',body)).statusCode,403);
    assert.equal((await f.request(f.other,'POST',body)).statusCode,409);
    assert.equal((await f.request(f.parent,'POST',body,false)).statusCode,403);
    for(const extra of [{mode:'disabled'},{mode:'auto',repository:'attacker/repo'},{mode:'auto',enabled:true},{mode:'auto',publicKeyFile:'/tmp/key'},{mode:'auto',operator:{}}])
      assert.equal((await f.request(f.parent,'POST',{action:'set_mode',...extra})).statusCode,400);
    assert.equal((await f.request(f.parent,'POST',body)).json().state,'queued');
    const queue=f.queue();assert.equal(queue.mode,'auto');assert.equal(queue.action,'set_mode');assert.match(queue.id,/^[a-f0-9-]{36}$/);assert.equal(Object.keys(queue).length,6);
    const view=(await f.request(f.parent)).json();assert.equal(view.mode,'notify');assert.equal(view.queuedMode.mode,'auto');assert.ok(!JSON.stringify(view).includes('DO_NOT_EXPOSE'));assert.equal(view.operator,undefined);
    assert.equal((await f.request(f.other)).json().configured,false);
  }finally{await f.close();}
});
test('old worker, disabled root gate and stale status cannot be used to enable UI mode control',async()=>{
  const f=await fixture();try{
    for(const patch of [{modeControlAllowed:false},{modeControlAllowed:undefined},{enabled:false,modeControlAllowed:true},{enabled:true,updatedAt:Date.now()-90000000}]){
      f.set(patch);assert.equal((await f.request(f.parent,'POST',{action:'set_mode',mode:'auto'})).statusCode,409);
    }
  }finally{await f.close();}
});
test('queued and root-journal pending requests never optimistically change applied mode or overwrite requests',async()=>{
  const f=await fixture();try{
    await f.request(f.parent,'POST',{action:'set_mode',mode:'auto'});const first=f.queue();
    assert.equal((await f.request(f.parent,'POST',{action:'set_mode',mode:'notify'})).json().state,'pending');assert.deepEqual(f.queue(),first);
    rmSync(join(f.dir,'update-request.json'));f.set({lastModeRequest:{id:first.id,mode:'auto',state:'pending',error:'configuration'}});
    const view=(await f.request(f.parent)).json();assert.equal(view.mode,'notify');assert.equal(view.lastModeRequest.state,'pending');
    assert.equal((await f.request(f.parent,'POST',{action:'install'})).json().state,'pending');
    f.set({mode:'auto',lastModeRequest:{id:first.id,mode:'auto',state:'applied',error:null}});
    assert.equal((await f.request(f.parent)).json().mode,'auto');
    assert.equal((await f.request(f.parent,'POST',{action:'set_mode',mode:'notify'})).json().state,'queued');
  }finally{await f.close();}
});
test('root rejection is visible and a fresh request can be queued without changing effective mode',async()=>{
  const f=await fixture();try{
    f.set({lastModeRequest:{id:randomUUID(),mode:'auto',state:'rejected',error:'expired'}});
    const view=(await f.request(f.parent)).json();assert.equal(view.mode,'notify');assert.equal(view.lastModeRequest.error,'expired');
    assert.equal((await f.request(f.parent,'POST',{action:'set_mode',mode:'auto'})).json().state,'queued');
  }finally{await f.close();}
});
