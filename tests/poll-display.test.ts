import {test} from 'node:test';
import assert from 'node:assert/strict';
import {randomBytes,randomUUID} from 'node:crypto';
import {readFileSync} from 'node:fs';
import React from 'react';
import {renderToStaticMarkup} from 'react-dom/server';
import {proto} from '@whiskeysockets/baileys';
import {normalizeIncoming} from '../src/connectors/baileys.js';
import {IngestionQueue} from '../src/connectors/ingestion.js';
import {pollDisplay,pollDisplayLimits,pollUnavailable} from '../src/poll-display.js';
import {PollMessage} from '../web/PollMessage';
import {fixture,raw,group,other} from './helpers/message-fixture.js';
const body=(name='Synthetic question')=>({name,options:[{optionName:'First'},{optionName:'Second'}],selectableOptionsCount:1});
const message=(id:string,extra:any={},field='pollCreationMessageV3')=>raw(id,{[field]:{...body(),...extra},messageContextInfo:{messageSecret:Buffer.alloc(32,7)}});

test('verified text poll variants project only question/options, including protobuf defaults',async()=>{
 const f=await fixture();try{
  for(const field of ['pollCreationMessage','pollCreationMessageV2','pollCreationMessageV3']){
   const incoming=proto.WebMessageInfo.fromObject(message(field,{encKey:Buffer.from('SECRET KEY'),contextInfo:{quotedMessage:{conversation:'CONTEXT SECRET'}},options:[{optionName:'First',optionHash:'SECRET HASH'},{optionName:'Second'}]},field));
   const m=normalizeIncoming(incoming as any)!;assert.equal(m.kind,'poll');assert.equal(m.text,'Synthetic question');assert.deepEqual(m.poll,{options:['First','Second']});assert.equal(m.raw,undefined);
   await f.service.ingest(f.a.familyId,m);const stored=f.get(field)!;
   assert.deepEqual(stored.poll,{options:['First','Second']});assert.equal(stored.raw,undefined);
   assert.ok(!JSON.stringify(stored).includes('SECRET'));assert.equal(stored.unsupportedReason,undefined);
  }
  assert.ok(!readFileSync(f.store.config.dataDir+'/app.sqlite').includes(Buffer.from('Synthetic question')));
  const response=await f.request('/messages');assert.equal(response.statusCode,200);
  assert.equal(response.json().messages.length,3);assert.ok(!response.body.includes('messageSecret'));assert.ok(!response.body.includes('CONTEXT SECRET'));
 }finally{await f.close();}
});

test('unknown versions, image polls, quizzes, votes and result snapshots retain precise non-content fallbacks',()=>{
 const cases:any[]=[
  {pollCreationMessageV4:{message:{pollCreationMessageV3:body('HIDDEN')}}},
  {pollCreationMessageV5:body('HIDDEN')},
  {pollCreationOptionImageMessage:{message:{imageMessage:{caption:'HIDDEN'}}}},
  {pollCreationMessageV3:{...body('HIDDEN'),pollType:1,correctAnswer:{optionName:'ANSWER SECRET'}}},
  {pollCreationMessageV3:{...body('HIDDEN'),correctAnswer:{optionName:'ANSWER SECRET'}}},
  {pollCreationMessageV3:{...body('HIDDEN'),pollContentType:2}},
  {pollCreationMessageV3:{...body('HIDDEN'),pollType:99}},
  {pollUpdateMessage:{vote:{encPayload:Buffer.from('VOTE SECRET')}}},
  {pollResultSnapshotMessage:{name:'HIDDEN',pollVotes:[{optionName:'ANSWER SECRET',optionVoteCount:5}]}},
  {pollResultSnapshotMessageV3:{name:'HIDDEN',pollVotes:[{optionName:'ANSWER SECRET',optionVoteCount:5}]}},
 ];
 for(const content of cases){
  const m=normalizeIncoming(raw('fallback',content))!;assert.equal(m.kind,'unsupported');assert.match(m.unsupportedReason!,/^poll-/);assert.equal(m.poll,undefined);assert.equal(m.raw,undefined);assert.ok(!JSON.stringify(m).includes('SECRET'));assert.ok(!m.text.includes('HIDDEN'));
 }
});

test('temporary envelopes and metadata are checked before inspecting poll contents or downloading',async()=>{
 const f=await fixture();try{
  const privatePoll={pollCreationMessageV3:body('PRIVATE')};
  for(const wrapper of ['ephemeralMessage','viewOnceMessage','viewOnceMessageV2','viewOnceMessageV2Extension']){
   for(const outer of ['documentWithCaptionMessage','associatedChildMessage','pollCreationMessageV4']){
    const m=normalizeIncoming(raw(wrapper+outer,{[outer]:{message:{[wrapper]:{message:privatePoll}}}}))!;
    assert.equal(m.restricted,true);assert.equal(m.poll,undefined);assert.equal(m.raw,undefined);assert.ok(!m.text.includes('PRIVATE'));
    await f.service.ingest(f.a.familyId,m);assert.equal(f.get(m.id)!.poll,undefined);
   }
  }
  for(const metadata of [{ephemeralDuration:60},{ephemeralOutOfSync:true}])assert.equal(normalizeIncoming({...message('meta',{name:'PRIVATE'}),...metadata})!.restricted,true);
  const expired=message('expired',{name:'PRIVATE',contextInfo:{expiration:60}});assert.equal(normalizeIncoming(expired)!.restricted,true);
  let downloads=0;const direct=normalizeIncoming(message('no-download'))!;
  await f.service.ingest(f.a.familyId,{...direct,raw:'PRIVATE RAW',bytes:Buffer.from('PRIVATE BYTES'),download:async()=>{downloads++;throw new Error('Forbidden');},poll:{options:['First','Second'],correctAnswer:'PRIVATE ANSWER',contextInfo:'PRIVATE CONTEXT'} as any});
  assert.equal(downloads,0);assert.equal(f.get(direct.id)!.mediaId,undefined);assert.ok(!JSON.stringify(f.get(direct.id)).includes('PRIVATE'));
 }finally{await f.close();}
});

test('poll fields are bounded and invalid/oversized input is not truncated into a different poll',async()=>{
 const limit=pollDisplayLimits;
 assert.ok(pollDisplay('q'.repeat(limit.question),Array.from({length:limit.options},()=> 'o'.repeat(limit.option))));
 assert.equal(pollDisplay('Question',Array(2)),undefined);
 for(const invalid of [
  {name:''},{name:'q'.repeat(limit.question+1)},{options:[]},{options:[{optionName:'one'}]},
  {options:Array.from({length:limit.options+1},()=>({optionName:'option'}))},
  {options:[{optionName:'o'.repeat(limit.option+1)},{optionName:'two'}]},
  {options:[{optionName:{}},{optionName:'two'}]},{options:[{optionName:' '},{optionName:'two'}]},
  {selectableOptionsCount:-1},{selectableOptionsCount:3},{selectableOptionsCount:1.5},
 ]){
  const m=normalizeIncoming(message('invalid',invalid))!;assert.equal(m.kind,'unsupported');assert.equal(m.unsupportedReason,'poll-invalid');assert.equal(m.poll,undefined);assert.equal(m.raw,undefined);
 }
 const f=await fixture();try{
  await f.service.ingest(f.a.familyId,{...normalizeIncoming(message('bad-service'))!,poll:{options:['x'.repeat(limit.option+1),'second']}});
  assert.equal(f.get('bad-service')!.kind,'unsupported');assert.equal(f.get('bad-service')!.poll,undefined);
 }finally{await f.close();}
});

test('queue byte budget includes option text even though provider raw is deliberately absent',async()=>{
 const consumed:any[]=[],notices:any[]=[];
 const queue=new IngestionQueue(async m=>{consumed.push(m);},()=>true,()=>true,n=>notices.push(n),{messages:10,bytes:1000,batches:8});
 const m=normalizeIncoming(message('large',{options:Array.from({length:20},()=>({optionName:'א'.repeat(100)}))}))!;
 queue.submit([m],true);await queue.settled();assert.equal(consumed.length,0);assert.ok(notices.some(n=>n.phase==='overload'));queue.dispose();
});

test('history/reverse arrival resolves quotes to poll question; replay cannot overwrite edits or deleted/restricted contents',async()=>{
 const f=await fixture();try{
  await f.ingest(raw('reply',{extendedTextMessage:{text:'Reply',contextInfo:{stanzaId:'poll',quotedMessage:{conversation:'NEVER COPY'}}}}));
  assert.equal(f.service.public(f.a.familyId,f.get('reply')!).reply!.available,false);
  await f.emit('messaging-history.set',{chats:[],contacts:[],messages:[message('poll')]});
  assert.equal(f.service.public(f.a.familyId,f.get('reply')!).reply!.text,'Synthetic question');assert.equal(f.get('poll')!.poll!.options.length,2);
  await f.emit('messages.update',[{key:{id:'edited',remoteJid:group},update:{message:{editedMessage:{message:{pollCreationMessageV3:body('Current question')}}},messageTimestamp:Math.floor(Date.now()/1000)}}]);
  await f.ingest({...message('edited',{name:'Old question'}),messageTimestamp:Math.floor(Date.now()/1000)-100});
  assert.equal(f.get('edited')!.text,'Current question');assert.deepEqual(f.get('edited')!.poll,{options:['First','Second']});
  f.service.update(f.a.familyId,group,'poll',{deleted:true});assert.equal(f.get('poll')!.poll,undefined);await f.ingest(message('poll'));assert.equal(f.get('poll')!.deleted,true);assert.equal(f.get('poll')!.poll,undefined);
  await f.ingest(raw('restricted',{ephemeralMessage:{message:{pollCreationMessageV3:body('PRIVATE')}}}));await f.ingest(message('restricted'));
  assert.equal(f.get('restricted')!.restricted,true);assert.equal(f.get('restricted')!.poll,undefined);
  await f.ingest(message('scrub'));await f.ingest(raw('scrub',{protocolMessage:{type:proto.Message.ProtocolMessage.Type.MESSAGE_EDIT,key:{id:'scrub',remoteJid:group},editedMessage:{viewOnceMessage:{message:{pollCreationMessageV3:body('PRIVATE')}}}}}));
  assert.equal(f.get('scrub')!.restricted,true);assert.equal(f.get('scrub')!.poll,undefined);
 }finally{await f.close();}
});

test('family/conversation and child grants govern poll contents; unknown chats never persist',async()=>{
 const f=await fixture({child:true});try{
  await f.ingest(message('same',{name:'FAMILY A QUESTION'}));
  await f.ingest({...message('same',{name:'OTHER CHAT QUESTION'}),key:{id:'same',remoteJid:other}});
  assert.equal(f.get('same')!.text,'FAMILY A QUESTION');
  const b=await f.store.createFamily('Other synthetic',{name:'other-parent',password:randomBytes(24).toString('hex')});f.store.syncGroups(b.familyId,[{id:group,title:'Other'}]);
  const session=(id:string)=>f.store.createSession(f.store.user(f.store.one('SELECT * FROM users WHERE id=?',id)));
  const child=session(f.a.childId),foreign=session(b.parentId);
  const read=(sid:string)=>f.app.inject({url:'/api/groups/'+encodeURIComponent(group)+'/messages',headers:{cookie:'sid='+sid}});
  assert.equal((await read(child.sid)).statusCode,404);
  await f.service.grant(f.a.familyId,f.a.childId,group,true);assert.match((await read(child.sid)).body,/FAMILY A QUESTION/);
  await f.service.grant(f.a.familyId,f.a.childId,group,false);assert.equal((await read(child.sid)).statusCode,404);
  const foreignResponse=await read(foreign.sid);assert.equal(foreignResponse.statusCode,200);assert.deepEqual(foreignResponse.json().messages,[]);
  await f.ingest({...message('unknown',{name:'DO NOT STORE'}),key:{id:'unknown',remoteJid:'unknown@g.us'}});assert.equal(f.store.getMessage(f.a.familyId,'unknown@g.us','unknown'),undefined);
 }finally{await f.close();}
});

test('UI renders untrusted text as text, with no voting/results controls and bounded DOM',()=>{
 const question='<img src="https://invalid.example/x" onerror="alert(1)">';
 const options=['<script>alert(1)</script>','"><svg onload="alert(2)">'];
 const html=renderToStaticMarkup(React.createElement(PollMessage,{question,options}));
 assert.ok(html.includes('&lt;img'));assert.ok(html.includes('&lt;script&gt;'));assert.ok(!/<(?:img|script|svg|iframe|input|button|a)\b/i.test(html));
 assert.ok(html.includes(pollUnavailable));assert.equal((html.match(/<li\b/g)||[]).length,2);assert.ok(html.includes('סקר לקריאה בלבד'));
 const oversized=renderToStaticMarkup(React.createElement(PollMessage,{question:'x'.repeat(pollDisplayLimits.question+1),options:Array(1000).fill('secret')}));
 assert.equal((oversized.match(/<li\b/g)||[]).length,0);assert.ok(!oversized.includes('secret'));
});

test('public projection discards unexpected poll properties and invalid legacy display payloads',async()=>{
 const f=await fixture();try{
  await f.ingest(message('public'));
  const m=f.get('public')!;
  const exposed=f.service.public(f.a.familyId,{...m,poll:{options:['First','Second'],correctAnswer:'SECRET',encKey:'SECRET',contextInfo:'SECRET',votes:[5,6]} as any});
  assert.deepEqual(exposed.poll,{options:['First','Second']});assert.ok(!JSON.stringify(exposed).includes('SECRET'));assert.equal((exposed as any).votes,undefined);
  const invalid=f.service.public(f.a.familyId,{...m,poll:{options:Array(1000).fill('HIDDEN')}});assert.equal(invalid.kind,'unsupported');assert.equal(invalid.poll,undefined);assert.ok(!JSON.stringify(invalid).includes('HIDDEN'));
 }finally{await f.close();}
});

test('poll display introduces no create, vote, results, media or provider-side action',async()=>{
 const f=await fixture();try{
  await f.ingest(message('readonly'));
  assert.deepEqual(f.service.public(f.a.familyId,f.get('readonly')!).actions,{reply:false,reactions:false,edit:false,delete:false});
  assert.equal((await f.request('/messages','POST',{clientId:randomUUID(),poll:body()})).statusCode,400);
  assert.equal((await f.request('/polls','POST',body())).statusCode,404);
  assert.equal((await f.request('/messages/readonly/reaction','POST',{emoji:'👍'})).statusCode,409);
  await assert.rejects(f.connector.send({id:'no-send',groupId:group,kind:'poll',text:'Question'} as any),/Poll sending unavailable/);
  assert.equal(f.sent.length,0);assert.deepEqual(f.connector.capabilities.polls,{identify:true,display:true,create:false,vote:false,results:false});
 }finally{await f.close();}
});
