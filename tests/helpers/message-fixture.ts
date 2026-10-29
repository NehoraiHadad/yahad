import {EventEmitter} from 'node:events';
import {mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {randomBytes,randomUUID} from 'node:crypto';
import type {WAMessage} from '@whiskeysockets/baileys';
import {BaileysConnector,normalizeIncoming} from '../../src/connectors/baileys.js';
import {createApp} from '../../src/server.js';
import type {Config} from '../../src/config.js';
export const group='120363001@g.us',other='120363002@g.us';
export const raw=(id:string,message:any,extra:any={}):WAMessage=>({key:{id,remoteJid:group,fromMe:false,participant:'100@lid'},pushName:'Synthetic',messageTimestamp:Math.floor(Date.now()/1000)-10,message,...extra});
export async function fixture(options:{child?:boolean}={}){
 const dir=mkdtempSync(join(tmpdir(),'yahad-message-fix-'));
 const config:Config={dataDir:dir,masterKey:randomBytes(32),connector:'mock',realAllowed:false,host:'127.0.0.1',port:3000,origin:'http://localhost:3000',secureCookie:false,maxMedia:1048576,maxStorage:67108864,childIdleMs:900000,sessionMs:3600000,diagnosticsEnabled:false};
 const ev=new EventEmitter(),sent:any[]=[];
 const socket:any={ev,groupFetchAllParticipating:async()=>({[group]:{id:group,subject:'Synthetic group'},[other]:{id:other,subject:'Synthetic other'}}),
 sendMessage:async(g:string,content:any,options:any={})=>{const id=options.messageId||randomUUID();sent.push({g,content,options});return raw(id,{conversation:content.text||''},{key:{id,remoteJid:g,fromMe:true},status:2});},
 presenceSubscribe:async()=>{},end:()=>{},updateMediaMessage:async()=>{throw new Error('Network forbidden in test');}};
 let connector!:BaileysConnector;
 const {app,store,service}=await createApp(config,{boot:false,factory:h=>connector=new BaileysConnector(h,()=>socket)});
 const a=await store.createFamily('Synthetic family',{name:'test-parent',password:randomBytes(24).toString('hex')},
   options.child?{name:'test-child',password:randomBytes(24).toString('hex')}:undefined);
 store.syncGroups(a.familyId,[{id:group,title:'Synthetic group'},{id:other,title:'Synthetic other'}]);
 await service.connect(a.familyId);ev.emit('connection.update',{connection:'open'});await new Promise<void>(r=>setImmediate(r));
 const s=store.createSession(store.user(store.one('SELECT * FROM users WHERE id=?',a.parentId)));
 const request=(path:string,method:any='GET',payload?:any)=>app.inject({url:'/api/groups/'+encodeURIComponent(group)+path,method,payload,headers:{origin:config.origin,cookie:'sid='+s.sid,'x-csrf-token':s.csrf}});
 const ingest=async(m:WAMessage)=>{const normalized=normalizeIncoming(m);if(normalized)await service.ingest(a.familyId,normalized);};
 const emit=async(name:string,data:any)=>{ev.emit(name,data);await new Promise<void>(r=>setImmediate(r));await connector.ingestion?.settled();};
 return{a,app,store,service,connector,ev,sent,request,ingest,emit,get:(id:string)=>store.getMessage(a.familyId,group,id),
 async close(){await app.close();rmSync(dir,{recursive:true,force:true});}};
}
