import {test} from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import {renderToStaticMarkup} from 'react-dom/server';
import {UpdateProgressStatus} from '../web/UpdatesPanel.js';
import {updateProgress,updateActionsDisabled,updateNeedsRepair,updatePhases} from '../web/updateProgress.js';
import {requestUpdate,updateStatus} from '../src/updates.js';
import type {WindowsUpdateTransport} from '../src/windows-update-client.js';
import type {Config} from '../src/config.js';
import type {User} from '../src/store.js';
import {createApp} from '../src/server.js';
import {privateFixtureDirectory} from './fixtures/private-directory.js';
import {randomBytes} from 'node:crypto';

const parent={role:'parent',familyId:'fixture-family',id:'fixture-parent'} as User;
const config={dataDir:'C:/unused-updater-policy-fixture'} as Config;
function fixture() {
  let snapshot:any={enabled:true,mode:'auto',modeControlAllowed:true,phase:'available',currentVersion:'0.6.0',latestVersion:'0.6.1',
    installTarget:{version:'0.6.1',fingerprint:'a'.repeat(64)},operator:{familyId:parent.familyId,userId:parent.id},
    checkedAt:Date.now(),updatedAt:Date.now(),error:null};
  const calls:unknown[]=[];
  const transport:WindowsUpdateTransport={status:async()=>snapshot,submit:async command=>{calls.push(command);return {state:'queued'};}};
  return {transport,calls,set:(fields:any)=>{snapshot={...snapshot,...fields};}};
}

test('full native phases survive status parsing and show actionable progress without repeated requests',async()=>{
  for(const phase of ['waiting_for_idle','updating_components','restarting_updater','preparing_data','releasing_effects','recovering'] as const){
    const f=fixture();f.set({phase});
    const status=await updateStatus(config,parent,f.transport);if(!status.configured)throw Error(`rejected native phase ${phase}`);
    assert.equal(status.phase,phase);const progress=updateProgress(status);
    assert.equal(progress.kind,'running');assert.equal(progress.active,true);assert.equal(progress.title,updatePhases[phase]);
    assert.equal(updateActionsDisabled(status,null,false,false),true);assert.equal(updateNeedsRepair(status),false);
    const html=renderToStaticMarkup(React.createElement(UpdateProgressStatus,{data:status}));
    assert.ok(html.includes(updatePhases[phase]));assert.match(html,/aria-live="polite"/);assert.doesNotMatch(html,/\d+%|aria-valuenow/);
  }
  const f=fixture();f.set({phase:'previous_restored',error:'health',
    lastRequest:{id:'11111111-1111-4111-8111-111111111111',action:'install',state:'failed',startedAt:1,finishedAt:Date.now(),error:'health'}});
  const status=await updateStatus(config,parent,f.transport);if(!status.configured)throw Error('rejected restored phase');
  const progress=updateProgress(status);assert.equal(progress.kind,'failure');assert.equal(progress.active,false);
  assert.equal(updateNeedsRepair(status),false);assert.equal(updateActionsDisabled(status,null,false,false),false);
  assert.match(progress.detail,/ניתן להשתמש בה/);assert.match(progress.detail,/אין צורך בתיקון ידני/);
  assert.ok(renderToStaticMarkup(React.createElement(UpdateProgressStatus,{data:status})).includes(updatePhases.previous_restored));
});

test('native Notify request is refused during recovery or pending work, like any other request',async()=>{
  for(const phase of ['recovery_required','rollback_pending_health','starting_runtime']){
    const f=fixture();f.set({phase,lastRequest:{id:'11111111-1111-4111-8111-111111111111',action:'install',state:'running',startedAt:1,finishedAt:null,error:null}});
    assert.equal(await requestUpdate(config,parent,'set_mode','notify',undefined,f.transport),phase==='starting_runtime'?'pending':'unavailable');assert.equal(f.calls.length,0);
  }
});
test('set_mode checks protected operator freshness and mode-control policy',async()=>{
  for(const patch of [{updatedAt:1},{enabled:false},{modeControlAllowed:false},{operator:{familyId:'other',userId:parent.id}}]){
    const f=fixture();f.set(patch);assert.notEqual(await requestUpdate(config,parent,'set_mode','notify',undefined,f.transport),'queued');assert.equal(f.calls.length,0);
  }
});
test('authenticated native update route executes the request and exposes the deferral route',async()=>{
  const f=fixture();const settings:Config={dataDir:privateFixtureDirectory('yachad-native-renewal-'),masterKey:randomBytes(32),connector:'mock',realAllowed:false,
    host:'127.0.0.1',port:3000,origin:'http://localhost:3000',secureCookie:false,maxMedia:1048576,maxStorage:67108864,childIdleMs:60000,sessionMs:3600000,diagnosticsEnabled:false};
  const {app,store}=await createApp(settings,{boot:false,windowsUpdates:f.transport});
  try{
    const password=randomBytes(24).toString('hex');const family=await store.createFamily('Fixture',{name:'parent',password},{name:'child',password});
    f.set({operator:{familyId:family.familyId,userId:family.parentId}});
    const session=store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?',family.parentId)));
    const response=await app.inject({method:'POST',url:'/api/parent/updates',payload:{action:'check'},headers:{origin:settings.origin,cookie:'sid='+session.sid,'x-csrf-token':session.csrf}});
    assert.equal(response.statusCode,200);assert.equal(response.json().state,'queued');assert.equal(f.calls.length,1);
    const deferred:unknown[]=[];f.transport.defer=async command=>{deferred.push(command);return {state:'queued'};};
    const postpone=await app.inject({method:'POST',url:'/api/parent/updates/defer',payload:{hours:1},headers:{origin:settings.origin,cookie:'sid='+session.sid,'x-csrf-token':session.csrf}});
    assert.equal(postpone.statusCode,200);assert.equal(deferred.length,1);
    assert.equal((deferred[0] as any).familyId,family.familyId);assert.equal((deferred[0] as any).userId,family.parentId);
    assert.ok(Math.abs((deferred[0] as any).untilUnix-Math.floor(Date.now()/1000)-3600)<5);
    const child=store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?',family.childId)));
    const denied=await app.inject({method:'POST',url:'/api/parent/updates/defer',payload:{hours:1},headers:{origin:settings.origin,cookie:'sid='+child.sid,'x-csrf-token':child.csrf}});
    assert.equal(denied.statusCode,403);assert.equal(deferred.length,1);
  }finally{await app.close();}
});
