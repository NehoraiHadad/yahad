import {test} from 'node:test';
import assert from 'node:assert/strict';
import {randomBytes,randomUUID} from 'node:crypto';
import React from 'react';
import {renderToStaticMarkup} from 'react-dom/server';
import {proto} from '@whiskeysockets/baileys';
import {normalizeIncoming} from '../src/connectors/baileys.js';
import {sharedTextLimit} from '../src/rich-content.js';
import {SharedContentMessage} from '../web/SharedContentMessage';
import {fixture,raw,group} from './helpers/message-fixture.js';
const card=(name='Synthetic contact',extra='')=>({displayName:'Provider label',vcard:`BEGIN:VCARD\r\nVERSION:3.0\r\nFN:${name}\r\nTEL;type=CELL;waid=100000000:+1 000 000 000\r\n${extra}END:VCARD\r\n`});
const location={degreesLatitude:32.1,degreesLongitude:34.8,name:'Synthetic place',address:'Synthetic address'};
const message=(id:string,kind='contact')=>raw(id,kind==='contact'?{contactMessage:card()}:{locationMessage:location});

test('contact and contact-array display only bounded names/numbers, never vcard extras or raw',async()=>{
 const f=await fixture();try{
  for(const [field,body]of Object.entries({contactMessage:card('Synthetic contact','URL:https://invalid.example/SECRET\r\nPHOTO;ENCODING=b:SECRET\r\nNOTE:SECRET\r\n'),contactsArrayMessage:{contacts:[card('First'),card('Second')]}})){
   const incoming=normalizeIncoming(proto.WebMessageInfo.fromObject(raw(field,{[field]:body})) as any)!;
   assert.equal(incoming.kind,'contact');assert.equal(incoming.raw,undefined);assert.ok(!incoming.text.includes('SECRET'));
   await f.service.ingest(f.a.familyId,incoming);assert.equal(f.get(field)!.raw,undefined);
  }
  assert.match(f.get('contactMessage')!.text,/Synthetic contact/);assert.match(f.get('contactsArrayMessage')!.text,/First[\s\S]*Second/);
  assert.match(f.get('contactMessage')!.text,/\+1 000 000 000/);assert.ok(!(await f.request('/messages')).body.includes('SECRET'));
  const folded=card('Long\\, Name');folded.vcard=folded.vcard.replace('Name','Na\r\n me');
  assert.match(normalizeIncoming(raw('fold',{contactMessage:folded}))!.text,/Long, Name/);
  const uri=card();uri.vcard=uri.vcard.replace('VERSION:3.0','VERSION:4.0').replace('+1 000 000 000','tel:+1000000000');
  assert.match(normalizeIncoming(raw('uri',{contactMessage:uri}))!.text,/\+1000000000/);
 }finally{await f.close();}
});

test('contact parser rejects unsupported encodings, malformed cards, excessive size/count and sparse collections',()=>{
 const bads:any[]=[{vcard:''},{...card(),vcard:card().vcard.replace('FN:','FN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:')},
  card('n'.repeat(257)),card('Name','TEL:javascript:alert(1)\r\n'),{...card(),vcard:'X'.repeat(16385)},
  {...card(),vcard:card().vcard.replace('END:VCARD','')},card('Name','FN:Duplicate\r\n'),
  card('Name',Array.from({length:9},(_,i)=>`TEL:+10000000${i}\r\n`).join(''))];
 for(const body of bads){const m=normalizeIncoming(raw('bad',{contactMessage:body}))!;assert.equal(m.kind,'unsupported');assert.equal(m.unsupportedReason,'contact-variant');assert.equal(m.raw,undefined);}
 for(const contacts of [[],Array(2),Array.from({length:17},()=>card())]){
  const m=normalizeIncoming(raw('array',{contactsArrayMessage:{contacts}}))!;assert.ok(m.restricted||m.unsupportedReason==='contact-variant');assert.equal(m.raw,undefined);
 }
});

test('static location requires explicit finite coordinates; ignores external URL/thumbnail/context and never implies live tracking',()=>{
 const m=normalizeIncoming(proto.WebMessageInfo.fromObject(raw('place',{locationMessage:{...location,url:'https://invalid.example/SECRET',jpegThumbnail:Buffer.from('SECRET'),contextInfo:{quotedMessage:{conversation:'SECRET'}}}}))as any)!;
 assert.equal(m.kind,'location');assert.match(m.text,/32.1, 34.8/);assert.match(m.text,/Synthetic address/);assert.ok(!JSON.stringify(m).includes('SECRET'));assert.equal(m.raw,undefined);
 for(const patch of [{degreesLatitude:NaN},{degreesLongitude:Infinity},{degreesLatitude:91},{degreesLongitude:-181},{name:'x'.repeat(257)},{address:'x'.repeat(1025)}])
  assert.equal(normalizeIncoming(raw('bad',{locationMessage:{...location,...patch}}))!.unsupportedReason,'location-invalid');
 for(const body of [{},{degreesLatitude:0},{degreesLongitude:0}])
  assert.equal(normalizeIncoming(proto.WebMessageInfo.fromObject(raw('missing',{locationMessage:body})) as any)!.unsupportedReason,'location-invalid');
 assert.equal(normalizeIncoming(raw('zero',{locationMessage:{degreesLatitude:0,degreesLongitude:0}}))!.kind,'location');
 for(const content of [{liveLocationMessage:{...location,caption:'SECRET'}},{locationMessage:{...location,isLive:true}}]){
  const live=normalizeIncoming(raw('live',content))!;assert.equal(live.kind,'unsupported');assert.equal(live.unsupportedReason,'live-location');assert.ok(!JSON.stringify(live).includes('32.1'));assert.equal(live.raw,undefined);
 }
});

test('privacy precedes shared content display including per-card expiration; no download or raw survives service ingestion',async()=>{
 const f=await fixture();try{
  for(const field of ['contactMessage','locationMessage']){
   for(const wrapper of ['ephemeralMessage','viewOnceMessage','viewOnceMessageV2','viewOnceMessageV2Extension']){
    const content={documentWithCaptionMessage:{message:{[wrapper]:{message:{[field]:field==='contactMessage'?card('SECRET'):location}}}}};
    const m=normalizeIncoming(raw(field+wrapper,content))!;assert.equal(m.restricted,true);assert.equal(m.raw,undefined);assert.ok(!JSON.stringify(m).includes('SECRET'));
    await f.service.ingest(f.a.familyId,m);assert.equal(f.get(m.id)!.restricted,true);
   }
  }
  const expiring=raw('expires',{contactsArrayMessage:{contacts:[{...card('SECRET'),contextInfo:{expiration:60}}]}});
  assert.equal(normalizeIncoming(expiring)!.restricted,true);
  let downloads=0;await f.service.ingest(f.a.familyId,{...normalizeIncoming(message('safe'))!,raw:'SECRET',bytes:Buffer.from('SECRET'),download:async()=>{downloads++;throw new Error('Forbidden');}});
  assert.equal(downloads,0);assert.equal(f.get('safe')!.raw,undefined);assert.equal(f.get('safe')!.mediaId,undefined);
 }finally{await f.close();}
});

test('shared content handles history, reverse quote arrival, edits and deletes without resurrection',async()=>{
 const f=await fixture();try{
  for(const kind of ['contact','location']){
   const id=kind;
   await f.ingest(raw('reply-'+id,{extendedTextMessage:{text:'Reply',contextInfo:{stanzaId:id,quotedMessage:{conversation:'SECRET'}}}}));
   assert.equal(f.service.public(f.a.familyId,f.get('reply-'+id)!).reply!.available,false);
   await f.emit('messaging-history.set',{chats:[],contacts:[],messages:[message(id,kind)]});
   assert.equal(f.get(id)!.kind,kind);assert.equal(f.service.public(f.a.familyId,f.get('reply-'+id)!).reply!.available,true);
   const edited=kind==='contact'?{contactMessage:card('Current contact')}:{locationMessage:{...location,name:'Current place'}};
   await f.ingest(raw('edit-'+kind,{protocolMessage:{type:proto.Message.ProtocolMessage.Type.MESSAGE_EDIT,key:{id,remoteJid:group},editedMessage:edited,timestampMs:Date.now()}}));
   await f.ingest(message(id,kind));assert.match(f.get(id)!.text,/Current/);
   f.service.update(f.a.familyId,group,id,{deleted:true});await f.ingest(message(id,kind));assert.equal(f.get(id)!.deleted,true);assert.equal(f.get(id)!.text,'');
   await f.ingest(raw('early-'+kind,{editedMessage:{message:edited}},{messageTimestamp:1700000100}));
   await f.ingest({...message('early-'+kind,kind),messageTimestamp:1700000000});assert.match(f.get('early-'+kind)!.text,/Current/);assert.equal(f.get('early-'+kind)!.time,1700000000000);
  }
 }finally{await f.close();}
});

test('shared content remains scoped to family and child grant, receive-only, and safe to render',async()=>{
 const f=await fixture({child:true});try{
  for(const kind of ['contact','location'])await f.ingest(message(kind,kind));
  const session=(id:string)=>f.store.createSession(f.store.user(f.store.one('SELECT * FROM users WHERE id=?',id)));
  const child=session(f.a.childId),read=(sid:string)=>f.app.inject({url:'/api/groups/'+encodeURIComponent(group)+'/messages',headers:{cookie:'sid='+sid}});
  assert.equal((await read(child.sid)).statusCode,404);await f.service.grant(f.a.familyId,f.a.childId,group,true);assert.match((await read(child.sid)).body,/Synthetic contact/);
  await f.service.grant(f.a.familyId,f.a.childId,group,false);assert.equal((await read(child.sid)).statusCode,404);
  const b=await f.store.createFamily('Other',{name:'Other',password:randomBytes(24).toString('hex')});f.store.syncGroups(b.familyId,[{id:group,title:'Other'}]);assert.deepEqual((await read(session(b.parentId).sid)).json().messages,[]);
  for(const kind of ['contact','location']){
   assert.deepEqual(f.service.public(f.a.familyId,f.get(kind)!).actions,{reply:false,reactions:false,edit:false,delete:false});
   assert.equal((await f.request('/messages/'+kind+'/reaction','POST',{emoji:'😀'})).statusCode,409);
   await assert.rejects(f.connector.send({id:'never',groupId:group,kind,text:'invalid'}as any),/Shared content sending unavailable/);
  }
  assert.equal((await f.request('/messages','POST',{clientId:randomUUID(),contact:card()})).statusCode,400);assert.equal(f.sent.length,0);
 }finally{await f.close();}
 const html=renderToStaticMarkup(React.createElement(SharedContentMessage,{text:'<img src="https://invalid.example/x" onerror="alert(1)">\n<script>alert(1)</script>'}));
 assert.match(html,/&lt;img/);assert.ok(!/<(?:img|script|iframe|a)\b/.test(html));
 assert.ok(!renderToStaticMarkup(React.createElement(SharedContentMessage,{text:'HIDDEN'.repeat(sharedTextLimit)})).includes('HIDDEN'));
});
