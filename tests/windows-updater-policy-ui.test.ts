import {test} from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import {renderToStaticMarkup} from 'react-dom/server';
import {NativeDelegationStatus,RejectedUpdateRequest,submitUpdateRequest,UpdateProgressStatus} from '../web/UpdatesPanel.js';
import {nativeNotifyAllowed,updateProgress,updateActionsDisabled,updateNeedsRepair,updatePhases} from '../web/updateProgress.js';
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
    checkedAt:Date.now(),updatedAt:Date.now(),error:null,delegation:{state:'expired',expiresUnix:1,remainingOperations:2}};
  const calls:unknown[]=[];
  const transport:WindowsUpdateTransport={status:async()=>snapshot,submit:async command=>{calls.push(command);return {state:'approval_required'};}};
  return {transport,calls,set:(fields:any)=>{snapshot={...snapshot,...fields};}};
}

test('renewal-required check remains a definite outcome and submits only once',async()=>{
  const f=fixture();assert.equal(await requestUpdate(config,parent,'check',undefined,undefined,f.transport),'approval_required');
  assert.equal(f.calls.length,1);
});

test('full native profile displays permanent scoped consent without legacy expiry or operation count',async()=>{
  const f=fixture();f.set({profile:'full-system-v1',delegation:null,systemGrant:{state:'active',untilRevoked:true,
    scopes:['application','database','authority','servicehost','owner','probe','handoff']}});
  const status=await updateStatus(config,parent,f.transport);if(!status.configured)throw Error('missing full status');
  const html=renderToStaticMarkup(React.createElement(NativeDelegationStatus,{data:status}));
  assert.match(html,/עדכונים אוטומטיים מלאים פעילים/);assert.match(html,/נשאר פעיל עד שתכבו/);assert.doesNotMatch(html,/נותרו .*עדכונים|תוקף האישור/);
  f.set({systemGrant:{state:'unavailable',untilRevoked:true,scopes:['application','database','authority','servicehost','owner','probe','handoff']}});
  const unknown=await updateStatus(config,parent,f.transport);if(!unknown.configured)throw Error('missing full unknown status');
  assert.match(renderToStaticMarkup(React.createElement(NativeDelegationStatus,{data:unknown})),/בדקו שוב לפני שליחת אישור נוסף/);
});
test('full native phases survive status parsing and show actionable progress without repeated requests',async()=>{
  for(const phase of ['waiting_for_idle','updating_components','restarting_updater','preparing_data','releasing_effects','recovering'] as const){
    const f=fixture();f.set({profile:'full-system-v1',phase,delegation:null,systemGrant:{state:'active',untilRevoked:true,
      scopes:['application','database','authority','servicehost','owner','probe','handoff']}});
    const status=await updateStatus(config,parent,f.transport);if(!status.configured)throw Error(`rejected native phase ${phase}`);
    assert.equal(status.phase,phase);const progress=updateProgress(status);
    assert.equal(progress.kind,'running');assert.equal(progress.active,true);assert.equal(progress.title,updatePhases[phase]);
    assert.equal(updateActionsDisabled(status,null,false,false),true);assert.equal(updateNeedsRepair(status),false);
    const html=renderToStaticMarkup(React.createElement(UpdateProgressStatus,{data:status}));
    assert.ok(html.includes(updatePhases[phase]));assert.match(html,/aria-live="polite"/);assert.doesNotMatch(html,/\d+%|aria-valuenow/);
  }
  const f=fixture();f.set({profile:'full-system-v1',phase:'previous_restored',error:'health',
    lastRequest:{id:'11111111-1111-4111-8111-111111111111',action:'install',state:'failed',startedAt:1,finishedAt:Date.now(),error:'health'}});
  const status=await updateStatus(config,parent,f.transport);if(!status.configured)throw Error('rejected restored phase');
  const progress=updateProgress(status);assert.equal(progress.kind,'failure');assert.equal(progress.active,false);
  assert.equal(updateNeedsRepair(status),false);assert.equal(updateActionsDisabled(status,null,false,false),false);
  assert.match(progress.detail,/ניתן להשתמש בה/);assert.match(progress.detail,/אין צורך בתיקון ידני/);
  assert.ok(renderToStaticMarkup(React.createElement(UpdateProgressStatus,{data:status})).includes(updatePhases.previous_restored));
});

test('native Notify contraction remains available during recovery or pending work',async()=>{
  for(const phase of ['recovery_required','rollback_pending_health','awaiting_local_approval','starting_runtime','activating']){
    const f=fixture();f.set({phase,lastRequest:{id:'11111111-1111-4111-8111-111111111111',action:'install',state:'running',startedAt:1,finishedAt:null,error:null}});
    assert.equal(await requestUpdate(config,parent,'set_mode','notify',undefined,f.transport),'approval_required');assert.equal(f.calls.length,1);
    assert.equal(await requestUpdate(config,parent,'check',undefined,undefined,f.transport),phase.includes('recovery')||phase==='rollback_pending_health'?'unavailable':'pending');
    assert.equal(f.calls.length,1);
  }
});
test('contraction still checks protected operator freshness and mode-control policy',async()=>{
  for(const patch of [{updatedAt:1},{enabled:false},{modeControlAllowed:false},{operator:{familyId:'other',userId:parent.id}}]){
    const f=fixture();f.set(patch);assert.notEqual(await requestUpdate(config,parent,'set_mode','notify',undefined,f.transport),'approval_required');assert.equal(f.calls.length,0);
  }
});
test('parent sees bounded renewal state without claiming that Auto is effective',async()=>{
  const f=fixture();const status=await updateStatus(config,parent,f.transport);if(!status.configured)throw Error('missing status');
  assert.equal(status.delegation?.state,'expired');assert.equal(status.delegation?.remainingOperations,2);
  const html=renderToStaticMarkup(React.createElement(NativeDelegationStatus,{data:status}));
  assert.match(html,/תוקף האישור פג/);assert.doesNotMatch(html,/הרשאת עדכון אוטומטי בתוקף/);
  assert.equal(nativeNotifyAllowed(status,false,false),true);
  assert.equal(nativeNotifyAllowed({...status,confirmation:undefined},false,false),false);
  assert.equal(nativeNotifyAllowed(status,true,false),false);assert.equal(nativeNotifyAllowed(status,false,true),false);
  f.set({delegation:{state:'busy',expiresUnix:null,remainingOperations:null}});
  const busy=await updateStatus(config,parent,f.transport);if(!busy.configured)throw Error('missing busy status');
  const unknown=renderToStaticMarkup(React.createElement(NativeDelegationStatus,{data:busy}));assert.match(unknown,/בודקים את מצב ההרשאה/);assert.doesNotMatch(unknown,/נותרו 0/);
  f.set({mode:'notify',delegation:null});const notify=await updateStatus(config,parent,f.transport);assert.equal(notify.configured,true);
});
test('UI treats approval-required HTTP response as a definite local rejection without retry',async()=>{
  const original=globalThis.fetch;let calls=0;
  globalThis.fetch=async()=>{calls++;return new Response(JSON.stringify({error:'UPDATES_APPROVAL_REQUIRED'}),{status:409,headers:{'content-type':'application/json'}});};
  try{await assert.rejects(submitUpdateRequest('check'),error=>error instanceof RejectedUpdateRequest&&error.message.includes('נדרש אישור מקומי חדש'));assert.equal(calls,1);}
  finally{globalThis.fetch=original;}
});
test('authenticated native update route returns actionable approval-required HTTP status',async()=>{
  const f=fixture();const settings:Config={dataDir:privateFixtureDirectory('yachad-native-renewal-'),masterKey:randomBytes(32),connector:'mock',realAllowed:false,
    host:'127.0.0.1',port:3000,origin:'http://localhost:3000',secureCookie:false,maxMedia:1048576,maxStorage:67108864,childIdleMs:60000,sessionMs:3600000,diagnosticsEnabled:false};
  const {app,store}=await createApp(settings,{boot:false,windowsUpdates:f.transport});
  try{
    const password=randomBytes(24).toString('hex');const family=await store.createFamily('Fixture',{name:'parent',password},{name:'child',password});
    f.set({operator:{familyId:family.familyId,userId:family.parentId}});
    const session=store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?',family.parentId)));
    const response=await app.inject({method:'POST',url:'/api/parent/updates',payload:{action:'check'},headers:{origin:settings.origin,cookie:'sid='+session.sid,'x-csrf-token':session.csrf}});
    assert.equal(response.statusCode,409);assert.equal(response.json().error,'UPDATES_APPROVAL_REQUIRED');assert.equal(f.calls.length,1);
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
