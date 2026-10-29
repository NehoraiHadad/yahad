import {test} from 'node:test';
import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import {normalizeIncoming} from '../src/connectors/baileys.js';
import {validReaction} from '../src/message-actions.js';
import {raw,fixture,group,other} from './helpers/message-fixture.js';
const poll={name:'Synthetic question',options:[{optionName:'A'},{optionName:'B'}],selectableOptionsCount:1};

test('a locally edited text is quoted with its current body before provider echo arrives',async()=>{
 const f=await fixture();try{
  await f.ingest(raw('own',{conversation:'Original'},{key:{id:'own',remoteJid:group,fromMe:true}}));const time=f.get('own')!.time;
  const edited=await f.request('/messages/own','PATCH',{text:'Current text'});assert.equal(edited.statusCode,200);
  const current=f.get('own')!;assert.equal(current.time,time);assert.equal(JSON.parse(current.raw!).message.conversation,'Current text');
  assert.equal(f.connector.historyAnchor(group,current),true);
  const response=await f.request('/messages','POST',{clientId:randomUUID(),text:'Reply',replyId:'own'});assert.equal(response.statusCode,200);
  assert.equal(f.sent.at(-1).options.quoted.message.conversation,'Current text');
  await f.emit('messages.update',[{key:{id:'own',remoteJid:group,fromMe:true},update:{message:{editedMessage:{message:{conversation:'Stale'}}},messageTimestamp:Math.floor(Date.now()/1000)-5}}]);
  assert.equal(f.get('own')!.text,'Current text');
 }finally{await f.close();}
});

test('quote eligibility and contents are rechecked after asynchronous preparation, before submission',async()=>{
 for(const change of ['delete','restrict','edit']){
  const f=await fixture();try{
   await f.ingest(raw('target',{conversation:'Original'}));
   let proceed!:()=>void,started!:()=>void;const preparing=new Promise<void>(r=>started=r),gate=new Promise<void>(r=>proceed=r);
   const send=f.connector.send.bind(f.connector);f.connector.send=async message=>{started();await gate;return send(message);};
   const pending=f.request('/messages','POST',{clientId:randomUUID(),text:'Reply',replyId:'target'});pending.then(()=>{});await preparing;
   if(change==='delete')f.service.update(f.a.familyId,group,'target',{deleted:true});
   else if(change==='restrict')await f.ingest(raw('target',{ephemeralMessage:{message:{conversation:'PRIVATE'}}}));
   else f.service.update(f.a.familyId,group,'target',{text:'Changed'});
   proceed();const response=await pending;assert.equal(response.statusCode,409);assert.equal(response.json().error,'ORIGINAL_UNAVAILABLE');assert.equal(f.sent.length,0);
  }finally{await f.close();}
 }
});

test('replies to unavailable originals are rejected before creating a pending message or sending',async()=>{
 const f=await fixture();try{
  await f.ingest(raw('poll',{pollCreationMessageV3:poll}));const clientId=randomUUID();
  const response=await f.request('/messages','POST',{clientId,text:'Reply',replyId:'poll'});
  assert.equal(response.statusCode,409);assert.equal(response.json().error,'ORIGINAL_UNAVAILABLE');assert.equal(f.sent.length,0);
  assert.equal(f.get('FC'+clientId.replaceAll('-','').toUpperCase()),undefined);
  const m=f.get('poll')!;assert.deepEqual(f.service.public(f.a.familyId,m).actions,{reply:false,reactions:false,edit:false,delete:false});
  await assert.rejects(f.connector.send({id:'direct',groupId:group,kind:'text',text:'Reply',reply:m}),/Original message unavailable/);assert.equal(f.sent.length,0);
 }finally{await f.close();}
});

test('unsupported, malformed, missing and mismatched originals never produce a reaction 500',async()=>{
 const f=await fixture();try{
  await f.ingest(raw('unsupported',{contactMessage:{displayName:'Synthetic'}}));
  let response=await f.request('/messages/unsupported/reaction','POST',{emoji:'👍'});assert.equal(response.statusCode,409);
  response=await f.request('/messages/missing/reaction','POST',{emoji:'👍'});assert.equal(response.statusCode,404);
  await f.ingest(raw('known',{conversation:'Original'}));
  for(const original of ['broken',JSON.stringify(raw('known',{conversation:'Wrong'},{key:{id:'known',remoteJid:other}}))]){
   f.store.putMessage(f.a.familyId,{...f.get('known')!,raw:original});
   response=await f.request('/messages/known/reaction','POST',{emoji:'👍'});assert.equal(response.statusCode,409);
  }
  assert.equal(f.sent.length,0);
 }finally{await f.close();}
});

test('complete emoji sequences are accepted consistently; arbitrary text is rejected before provider submission',async()=>{
 for(const emoji of ['', '👍','🧠','🇮🇱','1️⃣','🧑🏽‍❤️‍💋‍🧑🏻'])assert.equal(validReaction(emoji),true,emoji);
 for(const emoji of ['hello','👍👍','<script>', 'a'.repeat(65)])assert.equal(validReaction(emoji),false,emoji);
 const f=await fixture();try{
  await f.ingest(raw('known',{conversation:'Original'}));
  for(const emoji of ['🧠','🇮🇱','']){
   const response=await f.request('/messages/known/reaction','POST',{emoji});assert.equal(response.statusCode,200);assert.equal(f.sent.at(-1).content.react.text,emoji);
  }
  const before=f.sent.length;const response=await f.request('/messages/known/reaction','POST',{emoji:'not emoji'});assert.equal(response.statusCode,400);assert.equal(f.sent.length,before);
  const response2=await f.request('/messages','POST',{clientId:randomUUID(),text:'A real quote',replyId:'known'});assert.equal(response2.statusCode,200);assert.equal(f.sent.at(-1).options.quoted.key.id,'known');
 }finally{await f.close();}
});

test('poll types are identified honestly without exposing results, voting, secrets or embedded content',async()=>{
 const f=await fixture();try{
  for(const field of ['pollCreationMessage','pollCreationMessageV2','pollCreationMessageV3','pollUpdateMessage','pollResultSnapshotMessage']){
   await f.ingest(raw(field,{[field]:poll,messageContextInfo:{messageSecret:Buffer.alloc(32,7)}}));
   const m=f.get(field)!;assert.equal(m.kind,field.startsWith('pollCreation')?'poll':'unsupported');assert.equal(m.raw,undefined);
   const exposed=f.service.public(f.a.familyId,m);assert.equal(exposed.actions!.reactions,false);assert.equal((exposed as any).votes,undefined);
   if(m.kind==='poll'){assert.equal(m.text,poll.name);assert.deepEqual(exposed.poll?.options,['A','B']);}
   else assert.ok(!JSON.stringify(exposed).includes('Synthetic question'));
  }
  assert.deepEqual(f.connector.capabilities.polls,{identify:true,display:true,observedResults:true,create:false,vote:false,results:false});
  assert.equal((await f.request('/polls','POST',poll)).statusCode,404);
  const temporary=normalizeIncoming(raw('temp',{ephemeralMessage:{message:{pollCreationMessageV3:poll}}}))!;assert.equal(temporary.restricted,true);assert.equal(temporary.unsupportedReason,undefined);assert.equal(temporary.raw,undefined);
  await f.ingest(raw('unknown',{interactiveResponseMessage:{}}));assert.equal(f.get('unknown')!.unsupportedReason,undefined);
 }finally{await f.close();}
});

test('authorized same-ID redelivery can display a legacy poll only from newly supplied provider content',async()=>{
 const f=await fixture();try{
  await f.service.ingest(f.a.familyId,{id:'legacy',groupId:group,sender:'Synthetic',fromMe:false,time:Date.now(),kind:'unsupported',text:'Legacy generic explanation'});
  await f.ingest(raw('legacy',{pollCreationMessageV3:poll}));assert.equal(f.get('legacy')!.kind,'poll');assert.equal(f.get('legacy')!.raw,undefined);assert.equal(f.get('legacy')!.text,poll.name);
 }finally{await f.close();}
});
