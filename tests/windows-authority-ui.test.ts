import { test } from 'node:test';
import assert from 'node:assert/strict';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { UpdateProgressStatus } from '../web/UpdatesPanel.js';
import { updateProgress, requiresWebUpdateConfirmation, updateActionsDisabled, type ConfiguredUpdate } from '../web/updateProgress.js';
import { updateStatus, requestUpdate } from '../src/updates.js';
import { UnconfiguredWindowsUpdateTransport, type WindowsUpdateTransport } from '../src/windows-update-client.js';
import type { Config } from '../src/config.js';
import type { User } from '../src/store.js';
import { readFileSync } from 'node:fs';

const target = {version:'0.5.3',fingerprint:'a'.repeat(64)};
const user = {role:'parent',familyId:'synthetic-family',id:'synthetic-parent'} as User;
const config = {dataDir:'C:/does-not-exist-authority-ui-fixture'} as Config;
function fixture() {
  let snapshot: any = {enabled:true,mode:'auto',modeControlAllowed:true,phase:'available',currentVersion:'0.5.2',latestVersion:'0.5.3',
    operator:{familyId:user.familyId,userId:user.id},installTarget:target,checkedAt:Date.now(),updatedAt:Date.now(),error:null};
  const calls: unknown[]=[];
  const transport:WindowsUpdateTransport={status:async()=>snapshot,submit:async c=>{calls.push(c);return {state:'queued'};}};
  return {transport,calls,set:(v:any)=>{snapshot={...snapshot,...v};}};
}
test('native status preserves automatic mode and selects one native confirmation',async()=>{
  const f=fixture();const status=await updateStatus(config,user,f.transport);assert.equal(status.configured,true);
  if(!status.configured)throw Error();assert.equal(status.mode,'auto');assert.equal(status.confirmation,'native');assert.equal(requiresWebUpdateConfirmation(status),false);
  assert.equal(requiresWebUpdateConfirmation({...status,confirmation:undefined}),true,'legacy workers retain web confirmation');
});
test('waiting for local approval overrides stale success and local sending indicators',async()=>{
  const f=fixture();f.set({phase:'awaiting_local_approval'});const status=await updateStatus(config,user,f.transport);if(!status.configured)throw Error();
  const progress=updateProgress(status,{action:'install',state:'sending'});assert.equal(progress.kind,'queued');assert.equal(progress.active,true);
  assert.match(progress.title,/אישור/);assert.match(progress.detail,/אין צורך לשלוח/);assert.equal(updateActionsDisabled(status,null,false,false),true);
  const html=renderToStaticMarkup(React.createElement(UpdateProgressStatus,{data:status}));assert.match(html,/aria-live="polite"/);assert.doesNotMatch(html,/aria-valuenow|role="progressbar"/);
});
test('pending approval refuses duplicate install and automatic check requests',async()=>{
  const f=fixture();f.set({phase:'awaiting_local_approval'});
  assert.equal(await requestUpdate(config,user,'install',undefined,target,f.transport),'pending');
  assert.equal(await requestUpdate(config,user,'check',undefined,undefined,f.transport),'pending');assert.equal(f.calls.length,0);
});
test('native transport retains exact worker target and returns one queued submission',async()=>{
  const f=fixture();assert.equal(await requestUpdate(config,user,'install',undefined,{...target,fingerprint:'b'.repeat(64)},f.transport),'target_changed');assert.equal(f.calls.length,0);
  assert.equal(await requestUpdate(config,user,'install',undefined,target,f.transport),'queued');assert.equal(f.calls.length,1);assert.deepEqual((f.calls[0] as any).target,target);
});
test('native request outcome uncertainty does not retry or fall back to a file queue',async()=>{
  const f=fixture();let attempts=0;f.transport.submit=async()=>{attempts++;throw Error('reply lost after durable acceptance');};
  await assert.rejects(requestUpdate(config,user,'install',undefined,target,f.transport));assert.equal(attempts,1);
});
test('native mode switch is a policy request rather than implicit delegation',async()=>{
  const f=fixture();assert.equal(await requestUpdate(config,user,'set_mode','notify',undefined,f.transport),'queued');assert.equal((f.calls[0] as any).mode,'notify');
  assert.equal((await updateStatus(config,user,f.transport) as ConfiguredUpdate).mode,'auto','request does not change observed mode');
});
test('configured operator identity is checked before native requests',async()=>{
  const f=fixture();assert.deepEqual(await updateStatus(config,{...user,id:'other-parent'},f.transport),{configured:false});assert.equal(f.calls.length,0);
});
test('unconfigured native transport fails closed and cannot enable production',async()=>{
  const native=new UnconfiguredWindowsUpdateTransport();assert.deepEqual(await updateStatus(config,user,native),{configured:false});assert.equal(await requestUpdate(config,user,'check',undefined,undefined,native),'unavailable');
});
test('native UI install button submits directly while legacy UI keeps its confirmation',()=>{
  const source=readFileSync(new URL('../web/UpdatesPanel.tsx',import.meta.url),'utf8');
  assert.match(source,/if \(requiresWebUpdateConfirmation\(configured\)\) setConfirm/);
  assert.match(source,/else void request\('install', undefined, installTarget\)/);
});
