import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {Readable} from 'node:stream';
import {proto} from '@whiskeysockets/baileys';
import {normalizeIncoming,incomingMediaStream} from '../src/connectors/baileys.js';
import {fixture,raw,group} from './helpers/message-fixture.js';
const video={directPath:'/v/synthetic',mediaKey:Buffer.alloc(32,3),mimetype:'video/mp4'};
const message=(id:string,body:any=video)=>proto.WebMessageInfo.fromObject(raw(id,{ptvMessage:body}));

test('round video uses official PTV media contract after source and privacy validation',async()=>{
 const normalized=normalizeIncoming(message('ptv') as any)!;
 assert.equal(normalized.kind,'video');assert.equal(normalized.text,'הודעת וידאו עגולה');
 const safe=JSON.parse(normalized.raw!),bytes=readFileSync('fixtures/garden.mp4');let calls=0;
 const transport:any={direct:async(body:any,kind:string)=>{calls++;assert.equal(kind,'ptv');assert.equal(body.directPath,video.directPath);return Readable.from([bytes]);},message:async()=>{throw new Error('Wrong transport');}};
 const stream=await incomingMediaStream(safe,async()=>({}as any),transport),chunks:Buffer[]=[];
 for await(const chunk of stream)chunks.push(chunk);assert.deepEqual(Buffer.concat(chunks),bytes);
 for(const body of [{...video,url:'https://evil.example/x'},{...video,directPath:'//evil.example/x'},{...video,viewOnce:true}])
  await assert.rejects(incomingMediaStream(raw('bad',{ptvMessage:body}),async()=>({}as any),transport));
 assert.equal(calls,1);
 for(const field of ['imageMessage','documentMessage','videoMessage','audioMessage','stickerMessage']){
  for(const bad of [{...video,url:'https://evil.example/x'},{...video,directPath:'//evil.example/x'}]){
   const mixed=raw('mixed',{[field]:video,ptvMessage:bad});
   assert.equal(normalizeIncoming(mixed)!.kind,'unsupported');assert.equal(normalizeIncoming(mixed)!.raw,undefined);
   await assert.rejects(incomingMediaStream(mixed,async()=>({}as any),transport),/Ambiguous media/);
  }
 }
 assert.equal(calls,1);
});

test('round video persists encrypted and respects history, replay, deletion and child access',async()=>{
 const f=await fixture({child:true});try{
  const bytes=readFileSync('fixtures/garden.mp4'),incoming=normalizeIncoming(message('media') as any)!;
  let downloads=0;
  await f.service.ingest(f.a.familyId,{...incoming,history:{},download:async()=>{downloads++;return bytes;}});
  const m=f.get('media')!;assert.equal(m.kind,'video');assert.equal(m.mime,'video/mp4');assert.ok(m.mediaId);
  assert.ok(!readFileSync(f.store.mediaPath(f.a.familyId,group,m.mediaId!)).equals(bytes));
  const url='/api/groups/'+encodeURIComponent(group)+'/media/'+m.mediaId;
  assert.deepEqual((await f.app.inject({url,headers:{cookie:'sid='+f.store.createSession(f.store.user(f.store.one('SELECT * FROM users WHERE id=?',f.a.parentId))).sid}})).rawPayload,bytes);
  const child=f.store.createSession(f.store.user(f.store.one('SELECT * FROM users WHERE id=?',f.a.childId)));
  assert.equal((await f.app.inject({url,headers:{cookie:'sid='+child.sid}})).statusCode,404);
  await f.service.grant(f.a.familyId,f.a.childId,group,true);assert.equal((await f.app.inject({url,headers:{cookie:'sid='+child.sid}})).statusCode,200);
  await f.service.ingest(f.a.familyId,{...incoming,download:async()=>{downloads++;return bytes;}});assert.equal(downloads,1);
  f.service.update(f.a.familyId,group,m.id,{deleted:true});await f.ingest(message(m.id) as any);
  assert.equal(f.get(m.id)!.deleted,true);assert.equal(f.get(m.id)!.mediaId,undefined);
  const privateMessage=raw('private',{documentWithCaptionMessage:{message:{viewOnceMessageV2:{message:{ptvMessage:video}}}}});
  await f.ingest(privateMessage);assert.equal(f.get('private')!.restricted,true);assert.equal(f.get('private')!.raw,undefined);
 }finally{await f.close();}
});
