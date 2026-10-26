import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, readFileSync, readdirSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { Diagnostics, classifyFailure } from '../src/diagnostics.js';
import { createApp } from '../src/server.js';
import type { Config } from '../src/config.js';
function fixture() {
  const dir = mkdtempSync(join(tmpdir(), 'yachad-ops-'));
  const config: Config = { dataDir: dir, masterKey: randomBytes(32), connector:'mock', realAllowed:false,
    host:'127.0.0.1', port:3000, origin:'http://localhost:3000', secureCookie:false,
    maxMedia:1048576,maxStorage:64*1048576,childIdleMs:60000,sessionMs:3600000,
    updateStatusFile:join(dir,'root-updater-status.json') };
  return {dir, config, close: () => rmSync(dir,{recursive:true,force:true})};
}
test('diagnostics isolate families, sanitize failures, retain bounded encrypted history across restart', async () => {
  const f=fixture(); let now=Date.UTC(2026,9,26,12);
  try {
    const d=new Diagnostics(f.config,()=>now);
    d.record('family-a','media_write',300,{code:'EIO',message:'SECRET-CHAT-AND-KEY'});
    d.record('family-b','db_read',3);
    d.record('family-a','media_write',20,undefined,1048576);
    assert.throws(()=>d.measure('family-a','db_write',()=>{throw Object.assign(new Error('SECRET-SQL'),{errcode:13});}));
    const a=await d.report('family-a'), b=await d.report('family-b');
    assert.equal(a.metrics.find(x=>x.operation==='media_write')?.count,2);
    assert.equal(a.metrics.find(x=>x.operation==='media_write')?.errors.io,1);
    assert.equal(a.metrics.find(x=>x.operation==='db_write')?.errors.no_space,1);
    assert.deepEqual(b.metrics.map(x=>x.operation),['db_read']);
    assert.ok(!JSON.stringify(a).includes('SECRET'));
    await d.flush();
    const files=readdirSync(join(f.dir,'diagnostics'));
    assert.equal(files.length,2);
    for(const name of files) assert.ok(!readFileSync(join(f.dir,'diagnostics',name)).includes(Buffer.from('media_write')));
    const restarted=new Diagnostics(f.config,()=>now);
    assert.equal((await restarted.report('family-a')).metrics.find(x=>x.operation==='media_write')?.count,2);
    for(let hour=1;hour<=190;hour++) { now+=3600000; restarted.record('family-a','db_read',hour); }
    assert.equal((await restarted.report('family-a')).hours.length,168);
    await restarted.flush();
    await restarted.forget('family-a');
    restarted.record('family-a','api',1);
    await restarted.flush();
    assert.equal(readdirSync(join(f.dir,'diagnostics')).length,1);
    now+=8*86400000;
    assert.equal((await restarted.report('family-b')).metrics.length,0);
    await restarted.flush();
    assert.equal(readdirSync(join(f.dir,'diagnostics')).length,0);
  } finally { f.close(); }
});
test('diagnostics survive persistence failure, reset cannot be resurrected by in-flight flush, opt-out writes nothing', async()=>{
  const f=fixture();
  try {
    writeFileSync(join(f.dir,'diagnostics'),'not a directory');
    const d=new Diagnostics(f.config); d.record('a','db_write',250);
    await d.flush(); assert.ok((await d.report('a')).persistenceError);
    assert.equal((await d.report('a')).metrics[0].count,1);
    rmSync(join(f.dir,'diagnostics'));
    const flushing=d.flush(); await d.clear('a'); await flushing;
    assert.equal(readdirSync(join(f.dir,'diagnostics')).length,0);
    assert.equal((await d.report('a')).metrics.length,0);
    const disabled=new Diagnostics({...f.config,diagnosticsEnabled:false});
    disabled.record('b','api',100); await disabled.flush();
    assert.equal((await disabled.report('b')).metrics.length,0);
    assert.equal(classifyFailure({errcode:10+256,message:'SECRET'}),'io');
    assert.equal(classifyFailure({code:'EROFS'}),'readonly');
  } finally { f.close(); }
});
test('parent-only diagnostics/export/reset and operator-only update queue enforce sessions, family isolation and CSRF', async()=>{
  const f=fixture(); const {app,store}=await createApp(f.config,{boot:false});
  try {
    const password=randomBytes(24).toString('hex');
    const a=await store.createFamily('A',{username:'pa',password},{username:'ca',password});
    const b=await store.createFamily('B',{username:'pb',password},{username:'cb',password});
    function session(family:string,id:string){return store.createSession(store.user(store.one('SELECT * FROM users WHERE family_id=? AND id=?',family,id)));}
    const pa=session(a.familyId,a.parentId), ca=session(a.familyId,a.childId), pb=session(b.familyId,b.parentId);
    function req(s:typeof pa|null,url:string,method:any='GET',payload?:any,csrf=true){return app.inject({url,method,payload,headers:{origin:f.config.origin,...(s?{cookie:'sid='+s.sid,...(csrf?{'x-csrf-token':s.csrf}:{})}:{})}});}
    store.diagnostics.record(a.familyId,'media_write',350);
    store.diagnostics.record(b.familyId,'media_read',25);
    for(const url of ['/api/parent/diagnostics','/api/parent/diagnostics?download=1','/api/parent/updates']){
      assert.equal((await req(null,url)).statusCode,401);
      assert.equal((await req(ca,url)).statusCode,403);
    }
    assert.equal((await req(ca,'/api/parent/diagnostics','DELETE')).statusCode,403);
    assert.equal((await req(pa,'/api/parent/diagnostics','DELETE',undefined,false)).statusCode,403);
    const report=await req(pa,'/api/parent/diagnostics?download=1');
    assert.match(report.headers['content-disposition'] as string,/attachment/);
    assert.equal(report.json().metrics.filter((x:any)=>x.operation==='media_read').length,0);
    assert.equal((await req(pa,'/api/parent/updates','POST',{action:'install'})).statusCode,409);
    writeFileSync(f.config.updateStatusFile!,JSON.stringify({enabled:true,mode:'notify',operator:{familyId:a.familyId,userId:a.parentId},phase:'available',currentVersion:'0.1.0',latestVersion:'0.2.0',updatedAt:Date.now(),checkedAt:Date.now(),error:null,token:'NOT-TO-BROWSER'}));
    assert.equal((await req(pb,'/api/parent/updates')).json().configured,false);
    assert.equal((await req(pb,'/api/parent/updates','POST',{action:'install'})).statusCode,409);
    assert.equal((await req(ca,'/api/parent/updates','POST',{action:'install'})).statusCode,403);
    assert.equal((await req(pa,'/api/parent/updates','POST',{action:'install'},false)).statusCode,403);
    const state=(await req(pa,'/api/parent/updates')).json();
    assert.equal(state.configured,true); assert.ok(!JSON.stringify(state).includes('NOT-TO-BROWSER')); assert.equal(state.operator,undefined);
    assert.equal((await req(pa,'/api/parent/updates','POST',{action:'check',repository:'attacker/repo'})).statusCode,400);
    assert.equal((await req(pa,'/api/parent/updates','POST',{action:'install'})).json().state,'queued');
    const queued=JSON.parse(readFileSync(join(f.dir,'update-request.json'),'utf8'));
    assert.equal(queued.familyId,a.familyId); assert.equal(queued.userId,a.parentId);
    assert.equal((await req(pa,'/api/parent/updates','POST',{action:'install'})).json().state,'pending');
    assert.equal((await req(pa,'/api/parent/diagnostics','DELETE')).statusCode,200);
    assert.equal((await req(pa,'/api/parent/diagnostics')).json().metrics.length,0);
    assert.ok((await req(pb,'/api/parent/diagnostics')).json().metrics.length>0);
    assert.equal((await req(null,'/health/ready')).json().ok,true);
  } finally { await app.close(); f.close(); }
});
