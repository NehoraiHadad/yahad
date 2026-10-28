import {test} from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,readFileSync,rmSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {randomBytes} from 'node:crypto';
import {Readable} from 'node:stream';
import {proto, type WAMessage} from '@whiskeysockets/baileys';
import {normalizeIncoming,incomingMediaStream} from '../src/connectors/baileys.js';
import {createApp} from '../src/server.js';
import {MockConnector} from '../src/connectors/mock.js';
import type {Config} from '../src/config.js';
const group='120363001@g.us';
const sticker=(id:string,extra:any={})=>proto.WebMessageInfo.fromObject({key:{id,remoteJid:group,fromMe:false},pushName:'Synthetic sender',messageTimestamp:1700000000,
  message:{stickerMessage:{directPath:'/v/test-synthetic',mediaKey:Buffer.alloc(32,1),...extra}}}) as WAMessage;
class Quiet extends MockConnector{override async approved(){}}
async function fixture(){
 const dir=mkdtempSync(join(tmpdir(),'yachad-stickers-')),config:Config={dataDir:dir,masterKey:randomBytes(32),connector:'mock',realAllowed:false,host:'127.0.0.1',port:3000,origin:'http://localhost:3000',secureCookie:false,maxMedia:1048576,maxStorage:67108864,childIdleMs:900000,sessionMs:3600000,diagnosticsEnabled:false};
 const {app,store,service}=await createApp(config,{boot:false,factory:h=>new Quiet(h)});const password=randomBytes(24).toString('hex');
 const a=await store.createFamily('A',{name:'parent-a',password},{name:'child-a',password}),b=await store.createFamily('B',{name:'parent-b',password},{name:'child-b',password});
 await service.connect(a.familyId);await service.grant(a.familyId,a.childId,group,true);
 const session=(id:string)=>store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?',id)));
 const child=session(a.childId),other=session(b.childId);
 const get=(id:string,s=child)=>app.inject({url:'/api/groups/'+group+'/media/'+id,headers:{cookie:'sid='+s.sid}});
 return{a,b,store,service,get,other,async close(){await app.close();rmSync(dir,{recursive:true,force:true});}};
}
test('actual protobuf null defaults cannot misclassify static/animated stickers or other media as empty text',()=>{
 for(const animated of [false,true]){
  const raw=sticker('sticker',{isAnimated:animated});assert.equal(raw.message!.conversation,null);
  const normalized=normalizeIncoming(raw)!;assert.equal(normalized.kind,'sticker');assert.equal(normalized.text,'');assert.ok(normalized.raw);
 }
 for(const [field,kind]of [['imageMessage','image'],['audioMessage','audio'],['videoMessage','video'],['documentMessage','document']])
  assert.equal(normalizeIncoming(proto.WebMessageInfo.fromObject({key:{id:field,remoteJid:group},message:{[field]:{}}}) as WAMessage)!.kind,kind);
 const unsupported=normalizeIncoming(sticker('lottie',{isLottie:true}))!;assert.equal(unsupported.kind,'unsupported');assert.ok(unsupported.text);assert.equal(unsupported.raw,undefined);
});
test('sanitized directPath-only sticker uses supported lower-level transport after source validation',async()=>{
 const raw=JSON.parse(normalizeIncoming(sticker('direct'))!.raw!),bytes=readFileSync('fixtures/flower.webp');let direct=0,message=0;
 const transport:any={direct:async(body:any,kind:string)=>{direct++;assert.equal(kind,'sticker');assert.equal(body.directPath,'/v/test-synthetic');return Readable.from([bytes]);},message:async()=>{message++;return Readable.from([bytes]);}};
 const stream=await incomingMediaStream(raw,async()=>({}as any),transport),chunks:Buffer[]=[];for await(const part of stream)chunks.push(part);
 assert.deepEqual(Buffer.concat(chunks),bytes);assert.equal(direct,1);assert.equal(message,0);
 for(const patch of [{url:'https://evil.example/x'},{directPath:'//evil.example/x'}]){
  const malicious={...raw,message:{stickerMessage:{...raw.message.stickerMessage,...patch}}};await assert.rejects(incomingMediaStream(malicious,async()=>({}as any),transport));
 }
 assert.equal(direct,1);
});
test('incoming static and animated WebP are encrypted, served inline as WebP, and family-scoped',async()=>{
 const f=await fixture();try{
  for(const [index,path]of ['fixtures/flower.webp','fixtures/sticker-animated.webp'].entries()){
   const bytes=readFileSync(path),incoming=normalizeIncoming(sticker('media-'+index,{isAnimated:index===1}))!;
   await f.service.ingest(f.a.familyId,{...incoming,download:async()=>bytes});
   const stored=f.store.getMessage(f.a.familyId,group,incoming.id)!;assert.equal(stored.kind,'sticker');assert.equal(stored.mime,'image/webp');assert.ok(stored.mediaId);
   assert.ok(!readFileSync(f.store.mediaPath(f.a.familyId,group,stored.mediaId!)).equals(bytes));
   const response=await f.get(stored.mediaId!);assert.equal(response.statusCode,200);assert.equal(response.headers['content-type'],'image/webp');assert.match(String(response.headers['content-disposition']),/^inline/);assert.deepEqual(response.rawPayload,bytes);
   assert.equal((await f.get(stored.mediaId!,f.other)).statusCode,404);
  }
 }finally{await f.close();}
});
test('download failure or legacy empty text can be repaired on permitted same-ID redelivery without duplicating usable media',async()=>{
 const f=await fixture();try{
  const message=normalizeIncoming(sticker('retry'))!,bytes=readFileSync('fixtures/flower.webp');
  await f.service.ingest(f.a.familyId,{...message,download:async()=>{throw new Error('synthetic');}});assert.equal(f.store.getMessage(f.a.familyId,group,'retry')!.mediaId,undefined);
  let downloads=0;await f.service.ingest(f.a.familyId,{...message,download:async()=>{downloads++;return bytes;}});assert.ok(f.store.getMessage(f.a.familyId,group,'retry')!.mediaId);
  await f.service.ingest(f.a.familyId,{...message,download:async()=>{downloads++;return bytes;}});assert.equal(downloads,1);
  const legacy=normalizeIncoming(sticker('legacy'))!;f.store.putMessage(f.a.familyId,{...legacy,kind:'text',text:'',status:'unknown',reactions:{}});
  await f.service.ingest(f.a.familyId,{...legacy,bytes});assert.equal(f.store.getMessage(f.a.familyId,group,'legacy')!.kind,'sticker');assert.ok(f.store.getMessage(f.a.familyId,group,'legacy')!.mediaId);
 }finally{await f.close();}
});
test('unsupported sticker bytes stay an explicit explanation and are not stored as media',async()=>{
 const f=await fixture();try{
  await f.service.ingest(f.a.familyId,{...normalizeIncoming(sticker('invalid'))!,bytes:Buffer.from('unsupported synthetic bytes')});
  const message=f.store.getMessage(f.a.familyId,group,'invalid')!;assert.equal(message.kind,'unsupported');assert.ok(message.text);assert.equal(message.mediaId,undefined);assert.equal(message.raw,undefined);
 }finally{await f.close();}
});
test('sticker download and repair cannot cross revoked or replaced grant epochs',async()=>{
 const f=await fixture();let release!:(bytes:Buffer)=>void;try{
  const incoming=normalizeIncoming(sticker('revoked'))!;let started!:()=>void;const waiting=new Promise<void>(r=>started=r);
  const task=f.service.ingest(f.a.familyId,{...incoming,download:()=>{started();return new Promise<Buffer>(r=>release=r);}});await waiting;
  await f.service.grant(f.a.familyId,f.a.childId,group,false);await f.service.grant(f.a.familyId,f.a.childId,group,true);release(readFileSync('fixtures/flower.webp'));await task;
  assert.equal(f.store.getMessage(f.a.familyId,group,'revoked'),undefined);
  await f.service.grant(f.a.familyId,f.a.childId,group,false);let called=false;await f.service.ingest(f.a.familyId,{...incoming,download:async()=>{called=true;return Buffer.alloc(0);}});assert.equal(called,false);
 }finally{release?.(Buffer.alloc(0));await f.close();}
});
