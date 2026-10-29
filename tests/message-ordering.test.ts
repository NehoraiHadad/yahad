import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {randomBytes} from 'node:crypto';
import {proto} from '@whiskeysockets/baileys';
import {Store} from '../src/store.js';
import {ChatService} from '../src/service.js';
import {normalizeIncoming} from '../src/connectors/baileys.js';
import {pendingReactions,queueReaction} from '../src/message-reactions.js';
import {raw,fixture,group,other} from './helpers/message-fixture.js';
const quote=(id:string)=>({extendedTextMessage:{text:'Reply',contextInfo:{stanzaId:id,quotedMessage:{conversation:'DO NOT COPY'}}}});
const react=(target:string,emoji:string,time=Date.now(),actor='100@lid')=>raw('reaction',{reactionMessage:{key:{id:target,remoteJid:group},text:emoji,senderTimestampMs:time}},
 {key:{id:'reaction',remoteJid:group,participant:actor,fromMe:false}});

test('quotes resolve after reverse arrival without retaining embedded payload; deletion hides content',async()=>{
 const f=await fixture();try{
  await f.ingest(raw('reply',quote('target')));assert.equal(f.get('reply')!.replyId,'target');
  let response=(await f.request('/messages')).json().messages[0];assert.equal(response.reply.available,false);assert.ok(!JSON.stringify(response).includes('DO NOT COPY'));
  await f.ingest(raw('target',{conversation:'Original'}));response=(await f.request('/messages')).json().messages.find((m:any)=>m.id==='reply');
  assert.equal(response.reply.text,'Original');assert.equal(response.reply.available,true);
  f.service.update(f.a.familyId,group,'target',{deleted:true});assert.equal(f.service.public(f.a.familyId,f.get('reply')!).reply!.available,false);
  const cross:any=quote('target');cross.extendedTextMessage.contextInfo.remoteJid=other;
  await f.ingest(raw('cross',cross));assert.equal(f.get('cross')!.replyId,undefined);
 }finally{await f.close();}
});

test('early reactions survive a new Store, including removal, and reject stale history/live updates',async()=>{
 const f=await fixture();let reopened:Store|undefined;try{
  const time=Date.now()-5000;
  await f.ingest(react('target','👍',time));await f.ingest(react('target','',time+100));await f.ingest(react('target','❤',time-100));
  assert.equal(pendingReactions(f.store,f.a.familyId,group,'target')[0].emoji,'');
  reopened=new Store(f.store.config);const service=new ChatService(reopened);
  await service.ingest(f.a.familyId,normalizeIncoming(raw('target',{conversation:'Original'},{reactions:[{key:{remoteJid:group,participant:'100@lid'},text:'👍',senderTimestampMs:time-200}]}))!);
  assert.deepEqual(f.get('target')!.reactions,{});assert.equal(pendingReactions(f.store,f.a.familyId,group,'target').length,0);
  await f.ingest(react('target','❤',time+200));await f.ingest(react('target','👍',time));assert.equal(f.get('target')!.reactions['100@lid'],'❤');
  const response=(await f.request('/messages')).json().messages.find((m:any)=>m.id==='target');assert.equal(response.reactionTimes,undefined);assert.equal(response.reactions[0].emoji,'❤');
 }finally{reopened?.close();await f.close();}
});

test('historical reaction snapshots populate existing and new messages without regressing newer events',async()=>{
 const f=await fixture();try{
  const timestamp=Date.now()-1000;
  const original=raw('target',{conversation:'History'},{reactions:[{key:{remoteJid:group,participant:'100@lid'},text:'👍',senderTimestampMs:timestamp}]});
  await f.emit('messaging-history.set',{chats:[],contacts:[],messages:[original]});assert.equal(f.get('target')!.reactions['100@lid'],'👍');
  await f.ingest(react('target','❤',timestamp+100));await f.ingest(original);assert.equal(f.get('target')!.reactions['100@lid'],'❤');
  await f.emit('messages.reaction',[{key:{id:'target',remoteJid:group},reaction:{key:{participant:'200@lid'},text:'🧠',senderTimestampMs:timestamp+200}}]);
  assert.equal(f.get('target')!.reactions['200@lid'],'🧠');
 }finally{await f.close();}
});

test('pending reactions are encrypted, bounded, expiring and scoped to family/conversation/retention',async()=>{
 const f=await fixture();try{
  const b=await f.store.createFamily('Other synthetic',{name:'other-parent',password:randomBytes(24).toString('hex')});
  f.store.syncGroups(b.familyId,[{id:group,title:'Other'}]);
  queueReaction(f.store,b.familyId,group,{target:'same',actor:'PRIVATE ACTOR',emoji:'👍',time:1});
  assert.equal(pendingReactions(f.store,f.a.familyId,group,'same').length,0);
  const row=f.store.one('SELECT slot,body FROM pending_message_reactions WHERE family_id=?',b.familyId);assert.ok(!row.slot.includes('PRIVATE'));assert.ok(!Buffer.from(row.body).includes(Buffer.from('PRIVATE ACTOR')));
  for(let i=0;i<270;i++)queueReaction(f.store,f.a.familyId,group,{target:'target-'+i,actor:'100@lid',emoji:'👍',time:i});
  assert.equal(f.store.one('SELECT count(*) AS n FROM pending_message_reactions WHERE family_id=?',f.a.familyId).n,256);
  f.store.run('UPDATE pending_message_reactions SET received=0 WHERE family_id=?',f.a.familyId);
  assert.equal(pendingReactions(f.store,f.a.familyId,group,'target-269').length,0);
  queueReaction(f.store,f.a.familyId,other,{target:'same',actor:'100@lid',emoji:'❤',time:1});
  f.store.purgeGroup(f.a.familyId,other);assert.equal(f.store.one('SELECT count(*) AS n FROM pending_message_reactions WHERE family_id=?',f.a.familyId).n,0);
  assert.equal(pendingReactions(f.store,b.familyId,group,'same').length,1);
 }finally{await f.close();}
});

test('actual editedMessage update preserves original position; edit before history preserves edited content',async()=>{
 const f=await fixture();try{
  await f.ingest(raw('target',{conversation:'Original'}));const originalTime=f.get('target')!.time;
  const update=(id:string)=>[{key:{remoteJid:group,id},update:{message:{editedMessage:{message:{conversation:'Changed'}}},messageTimestamp:Math.floor(Date.now()/1000)}}];
  await f.emit('messages.update',update('target'));assert.equal(f.get('target')!.text,'Changed');assert.equal(f.get('target')!.edited,true);assert.equal(f.get('target')!.time,originalTime);
  await f.emit('messages.update',update('late'));await f.ingest(raw('late',{conversation:'Old content'},{messageTimestamp:Math.floor(Date.now()/1000)-100,pushName:'Original sender'}));
  assert.equal(f.get('late')!.text,'Changed');assert.equal(f.get('late')!.sender,'Original sender');assert.ok(f.get('late')!.time<originalTime);
 }finally{await f.close();}
});

test('early deletion from each provider route persists a tombstone and cannot resurrect through replay',async()=>{
 const f=await fixture();try{
  await f.emit('messages.update',[{key:{remoteJid:group,id:'update'},update:{message:null}}]);
  await f.emit('messages.delete',{keys:[{remoteJid:group,id:'delete'}]});
  await f.ingest(raw('protocol',{protocolMessage:{type:proto.Message.ProtocolMessage.Type.REVOKE,key:{remoteJid:group,id:'revoke'}}}));
  for(const id of ['update','delete','revoke']){await f.ingest(raw(id,{conversation:'Old private content'}));assert.equal(f.get(id)!.deleted,true);assert.equal(f.get(id)!.raw,undefined);assert.equal(f.get(id)!.text,'');}
 }finally{await f.close();}
});

test('in-flight media cannot overwrite a deletion or temporary-content decision',async()=>{
 const f=await fixture();try{
  for(const restricted of [false,true]){
   const id='race-'+restricted;let finish!:(bytes:Buffer)=>void;
   const pending=f.service.ingest(f.a.familyId,{...normalizeIncoming(raw(id,{imageMessage:{caption:'Old'}}))!,download:()=>new Promise<Buffer>(r=>finish=r)});
   await Promise.resolve();
   if(restricted)await f.ingest(raw(id,{ephemeralMessage:{message:{imageMessage:{caption:'PRIVATE'}}}}));else f.service.update(f.a.familyId,group,id,{deleted:true});
   finish(readFileSync('fixtures/flower.webp'));await pending;
   assert.equal(f.get(id)!.mediaId,undefined);assert.equal(f.get(id)!.raw,undefined);assert.ok(f.get(id)!.deleted||f.get(id)!.restricted);
  }
 }finally{await f.close();}
});
