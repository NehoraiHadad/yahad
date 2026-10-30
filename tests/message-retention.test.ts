import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {proto} from '@whiskeysockets/baileys';
import {normalizeIncoming,validateMediaSource} from '../src/connectors/baileys.js';
import {raw,fixture,group,other} from './helpers/message-fixture.js';

test('temporary wrappers and flags at every supported nesting depth never retain payload or media',()=>{
 for(const marker of ['ephemeralMessage','viewOnceMessage','viewOnceMessageV2','viewOnceMessageV2Extension']){
  for(const wrapper of ['documentWithCaptionMessage','editedMessage','associatedChildMessage']){
   const message:any={[wrapper]:{message:{[marker]:{message:{imageMessage:{caption:'PRIVATE SYNTHETIC',directPath:'/synthetic'}}}}}};
   const m=normalizeIncoming(raw('temporary',message))!;
   assert.equal(m.restricted,true);assert.equal(m.raw,undefined);assert.equal(m.kind,'unsupported');assert.ok(!m.text.includes('PRIVATE'));
   assert.throws(()=>validateMediaSource(raw('temporary',message)),/Temporary/);
  }
 }
 for(const field of ['imageMessage','audioMessage','videoMessage']){
  const m=normalizeIncoming(raw(field,{[field]:{viewOnce:true,caption:'PRIVATE'}}))!;assert.equal(m.raw,undefined);assert.equal(m.restricted,true);
 }
 assert.equal(normalizeIncoming(raw('key',{conversation:'PRIVATE'},{key:{id:'key',remoteJid:group,isViewOnce:true}}))!.restricted,true);
 for(const metadata of [{ephemeralDuration:60},{ephemeralStartTimestamp:1700000000},{ephemeralOutOfSync:true}]){
  const m=normalizeIncoming(raw('metadata',{conversation:'PRIVATE'},metadata))!;assert.equal(m.restricted,true);assert.equal(m.raw,undefined);
 }
 const expired=normalizeIncoming(raw('expiration',{editedMessage:{message:{extendedTextMessage:{text:'PRIVATE',contextInfo:{expiration:60}}}}}))!;
 assert.equal(expired.restricted,true);assert.equal(expired.raw,undefined);
});

test('legacy temporary rows are scrubbed lazily and cannot be quoted or served as media',async()=>{
 const f=await fixture();try{
  const legacy={...normalizeIncoming(raw('legacy',{imageMessage:{caption:'Visible'}}))!,status:'sent' as const,reactions:{},
    raw:JSON.stringify(raw('legacy',{documentWithCaptionMessage:{message:{ephemeralMessage:{message:{imageMessage:{caption:'PRIVATE LEGACY'}}}}}})),text:'PRIVATE LEGACY'};
  f.store.putMessage(f.a.familyId,legacy);const media='legacy-media';
  f.store.putMedia(f.a.familyId,group,media,legacy.id,readFileSync('fixtures/flower.webp'));f.store.putMessage(f.a.familyId,{...legacy,mediaId:media});
  assert.equal(f.connector.originalAvailable(legacy),false);assert.ok(!JSON.stringify(f.service.public(f.a.familyId,legacy)).includes('PRIVATE LEGACY'));
  const message=f.get('legacy')!;assert.equal(message.restricted,true);assert.equal(message.raw,undefined);assert.equal(message.mediaId,undefined);assert.ok(!message.text.includes('PRIVATE LEGACY'));
  assert.equal(f.store.getMedia(f.a.familyId,group,media),undefined);assert.equal(f.get('legacy')!.restricted,true);
 }finally{await f.close();}
});

test('privacy detection ignores embedded quote payload and caps excessive message wrappers',()=>{
 const message:any={extendedTextMessage:{text:'Visible',contextInfo:{stanzaId:'old',quotedMessage:{viewOnceMessage:{message:{imageMessage:{caption:'PRIVATE'}}}}}}};
 const m=normalizeIncoming(raw('quote',message))!;assert.equal(m.kind,'text');assert.equal(m.text,'Visible');assert.ok(!m.raw!.includes('PRIVATE'));
 let deep:any={conversation:'PRIVATE'};for(let i=0;i<10;i++)deep={editedMessage:{message:deep}};
 assert.equal(normalizeIncoming(raw('deep',deep))!.restricted,true);
});

test('temporary protocol edits scrub existing media and cannot be restored by ordinary replay',async()=>{
 const f=await fixture();try{
  const original=normalizeIncoming(raw('target',{imageMessage:{caption:'Original'}}))!;
  await f.service.ingest(f.a.familyId,{...original,bytes:readFileSync('fixtures/flower.webp')});
  const media=f.get('target')!.mediaId!;assert.ok(media);
  await f.ingest(raw('edit',{protocolMessage:{type:proto.Message.ProtocolMessage.Type.MESSAGE_EDIT,key:{remoteJid:group,id:'target'},editedMessage:{documentWithCaptionMessage:{message:{viewOnceMessage:{message:{imageMessage:{caption:'PRIVATE'}}}}}}}}));
  const m=f.get('target')!;assert.equal(m.restricted,true);assert.equal(m.raw,undefined);assert.equal(m.mediaId,undefined);assert.equal(f.store.getMedia(f.a.familyId,group,media),undefined);
  await f.ingest(raw('target',{conversation:'PRIVATE REPLAY'}));assert.equal(f.get('target')!.restricted,true);assert.ok(!f.get('target')!.text.includes('PRIVATE'));
  const response=await f.request('/messages');assert.ok(!response.body.includes('PRIVATE'));
  await f.ingest(raw('cross',{protocolMessage:{type:proto.Message.ProtocolMessage.Type.MESSAGE_EDIT,key:{remoteJid:other,id:'target'},editedMessage:{ephemeralMessage:{message:{conversation:'PRIVATE'}}}}}));
  assert.equal(f.get('cross'),undefined);
 }finally{await f.close();}
});

test('restricted reaction envelopes cannot mutate a target and restricted messages cannot download',async()=>{
 const f=await fixture();try{
  await f.ingest(raw('target',{conversation:'Visible'}));
  await f.emit('messages.upsert',{type:'notify',messages:[raw('private',{ephemeralMessage:{message:{reactionMessage:{key:{id:'target',remoteJid:group},text:'👍'}}}})]});
  assert.deepEqual(f.get('target')!.reactions,{});assert.equal(f.get('private')!.restricted,true);
  await f.emit('messages.upsert',{type:'notify',messages:[raw('media',{imageMessage:{viewOnce:true,directPath:'/no-network',caption:'PRIVATE'}})]});
  assert.equal(f.get('media')!.mediaId,undefined);assert.equal(f.get('media')!.raw,undefined);
 }finally{await f.close();}
});
