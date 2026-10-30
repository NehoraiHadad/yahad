import {test} from 'node:test';
import assert from 'node:assert/strict';
import {proto} from '@whiskeysockets/baileys';
import {normalizeIncoming} from '../src/connectors/baileys.js';
import {fixture,raw,group} from './helpers/message-fixture.js';
const cases:any[]=[
 {buttonsResponseMessage:{selectedDisplayText:'Selected button',selectedButtonId:'PRIVATE_ID'}},
 {templateButtonReplyMessage:{selectedDisplayText:'Template reply',selectedId:'PRIVATE_ID'}},
 {listResponseMessage:{title:'Chosen row',description:'Row description',singleSelectReply:{selectedRowId:'PRIVATE_ID'}}},
 {interactiveResponseMessage:{body:{text:'Flow reply'},nativeFlowResponseMessage:{name:'PRIVATE_ID',paramsJson:'PRIVATE_JSON'}}},
 {buttonsMessage:{text:'Title',contentText:'Body',footerText:'Footer',buttons:[{type:1,buttonId:'PRIVATE_ID',buttonText:{displayText:'Button label'}}]}},
 {listMessage:{title:'List',description:'Body',buttonText:'View',sections:[{title:'Section',rows:[{title:'Row',description:'Detail',rowId:'PRIVATE_ID'}]}]}},
 {templateMessage:{hydratedTemplate:{hydratedTitleText:'Title',hydratedContentText:'Template body',hydratedButtons:[{urlButton:{displayText:'Open',url:'https://invalid.example/PRIVATE_URL'}},{quickReplyButton:{displayText:'Reply',id:'PRIVATE_ID'}}]}}},
 {interactiveMessage:{header:{title:'Header'},body:{text:'Interactive body'},nativeFlowMessage:{buttons:[{name:'PRIVATE_ID',buttonParamsJson:'PRIVATE_JSON'}],messageParamsJson:'PRIVATE_JSON'}}},
];

test('known protobuf interactive formats project text/labels only, without IDs, JSON, media or active commands',async()=>{
 const f=await fixture();try{
  for(const [i,content]of cases.entries()){
   const incoming=normalizeIncoming(proto.WebMessageInfo.fromObject(raw('format-'+i,content))as any)!;
   assert.equal(incoming.kind,'interactive');assert.equal(incoming.raw,undefined);assert.ok(!JSON.stringify(incoming).includes('PRIVATE'));
   assert.match(incoming.text,/תצוגת טקסט בלבד/);await f.service.ingest(f.a.familyId,incoming);
   const exposed=f.service.public(f.a.familyId,f.get(incoming.id)!);assert.deepEqual(exposed.actions,{reply:false,reactions:false,edit:false,delete:false});
   assert.equal((await f.request('/messages/'+incoming.id+'/reaction','POST',{emoji:'😀'})).statusCode,409);
  }
  assert.ok(!(await f.request('/messages')).body.includes('PRIVATE'));assert.equal(f.sent.length,0);
  await assert.rejects(f.connector.send({id:'invalid',groupId:group,kind:'interactive',text:'Bad'}as any),/Shared content sending unavailable/);
 }finally{await f.close();}
});

test('unknown, ambiguous, oversized, malformed, media and carousel variants give bounded precise fallbacks',()=>{
 const bads:any[]=[
  {interactiveMessage:{}},{interactiveResponseMessage:{}},{buttonsResponseMessage:{selectedButtonId:'PRIVATE'}},
  {buttonsMessage:{contentText:'HIDDEN',imageMessage:{url:'https://invalid.example/PRIVATE'},buttons:[]}},
  {buttonsMessage:{contentText:'HIDDEN',buttons:[{nativeFlowInfo:{name:'PRIVATE'}}]}},
  {buttonsMessage:{contentText:'x'.repeat(4097),buttons:[]}},
  {buttonsMessage:{contentText:'HIDDEN',buttons:Array(11).fill({buttonText:{displayText:'one'}})}},
  {listMessage:{description:'HIDDEN',sections:[{rows:Array(33).fill({title:'one'})}]}},
  {templateMessage:{hydratedTemplate:{hydratedContentText:'HIDDEN',imageMessage:{}}}},
  {templateMessage:{fourRowTemplate:{content:{text:'HIDDEN'}}}},
  {templateMessage:{hydratedTemplate:{hydratedContentText:'HIDDEN',maskLinkedDevices:true}}},
  {interactiveMessage:{body:{text:'HIDDEN'},carouselMessage:{cards:[]}}},
  {interactiveMessage:{body:{text:'HIDDEN'},header:{hasMediaAttachment:true}}},
  {...cases[0],...cases[1]},
 ];
 for(const content of bads){const m=normalizeIncoming(raw('bad',content))!;assert.equal(m.kind,'unsupported');assert.equal(m.unsupportedReason,'interactive-variant');assert.equal(m.raw,undefined);assert.ok(!JSON.stringify(m).includes('HIDDEN'));assert.ok(m.text.length<300);}
});

test('interactive content privacy is checked through wrappers, own nested media, templates and carousel cards',async()=>{
 const f=await fixture();try{
  const privateBodies:any[]=[
   {ephemeralMessage:{message:cases[4]}},
   {documentWithCaptionMessage:{message:{viewOnceMessageV2:{message:cases[6]}}}},
   {buttonsMessage:{contentText:'PRIVATE',buttons:[],imageMessage:{viewOnce:true}}},
   {interactiveMessage:{body:{text:'PRIVATE'},header:{imageMessage:{contextInfo:{expiration:60}}}}},
   {templateMessage:{hydratedTemplate:{hydratedContentText:'PRIVATE',imageMessage:{viewOnce:true}}}},
   {templateMessage:{interactiveMessageTemplate:{body:{text:'PRIVATE'},header:{videoMessage:{viewOnce:true}}}}},
   {interactiveMessage:{body:{text:'PRIVATE'},carouselMessage:{cards:[{header:{imageMessage:{viewOnce:true}}}]}}},
  ];
  for(const [i,content]of privateBodies.entries()){
   const m=normalizeIncoming(raw('private-'+i,content))!;assert.equal(m.restricted,true);assert.equal(m.raw,undefined);assert.ok(!JSON.stringify(m).includes('PRIVATE'));
   await f.service.ingest(f.a.familyId,m);await f.ingest(raw(m.id,cases[0]));assert.equal(f.get(m.id)!.restricted,true);
  }
  const quoted={buttonsResponseMessage:{selectedDisplayText:'Public choice',contextInfo:{quotedMessage:{viewOnceMessage:{message:{conversation:'PRIVATE_QUOTE'}}}}}};
  const m=normalizeIncoming(raw('public',quoted))!;assert.equal(m.kind,'interactive');assert.ok(!JSON.stringify(m).includes('PRIVATE_QUOTE'));
 }finally{await f.close();}
});

test('interactive history, edits and deletes remain scoped and cannot resurrect old text',async()=>{
 const f=await fixture({child:true});try{
  await f.emit('messaging-history.set',{chats:[],contacts:[],messages:[raw('history',cases[0])]});assert.match(f.get('history')!.text,/Selected button/);
  await f.service.ingest(f.a.familyId,{...normalizeIncoming(raw('history',cases[1]))!,edited:true,editTime:Date.now()});
  await f.ingest(raw('history',cases[0]));assert.match(f.get('history')!.text,/Template reply/);
  f.service.update(f.a.familyId,group,'history',{deleted:true});await f.ingest(raw('history',cases[0]));assert.equal(f.get('history')!.text,'');
  const session=f.store.createSession(f.store.user(f.store.one('SELECT * FROM users WHERE id=?',f.a.childId)));
  const read=()=>f.app.inject({url:'/api/groups/'+encodeURIComponent(group)+'/messages',headers:{cookie:'sid='+session.sid}});
  assert.equal((await read()).statusCode,404);await f.service.grant(f.a.familyId,f.a.childId,group,true);assert.equal((await read()).statusCode,200);await f.service.grant(f.a.familyId,f.a.childId,group,false);assert.equal((await read()).statusCode,404);
 }finally{await f.close();}
});
