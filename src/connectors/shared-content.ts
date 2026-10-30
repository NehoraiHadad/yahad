import type {proto} from '@whiskeysockets/baileys';
import type {Incoming} from './types.js';
import {sharedText} from '../rich-content.js';

type Projection=Pick<Incoming,'kind'|'text'|'raw'|'unsupportedReason'>;
const fallback=(reason:Incoming['unsupportedReason'],text:string):Projection=>({kind:'unsupported',text,unsupportedReason:reason,raw:undefined});
const contactFallback=()=>fallback('contact-variant','כרטיס איש קשר בפורמט שאינו מוצג כאן. אפשר לפתוח אותו ב־WhatsApp.');
const plain=(value:unknown,max:number)=>typeof value==='string' && value.length<=max && !/[\u0000-\u0008\u000b-\u001f\u007f]/.test(value) ? value : undefined;

/** Deliberately bounded display subset, not a general vCard importer.
 * Keep FN/TEL only, ignore URLs/photos/address/notes and provider context. */
function contact(body:proto.Message.IContactMessage):string|undefined {
 if(typeof body.vcard!=='string' || body.vcard.length>16_384)return;
 const lines=body.vcard.replace(/\r\n/g,'\n').replace(/\n[ \t]/g,'').split('\n');
 if(lines.shift()!=='BEGIN:VCARD')return;
 while(lines.at(-1)==='')lines.pop();
 if(lines.pop()!=='END:VCARD' || !lines.some(l=>/^VERSION:(?:2\.1|3\.0|4\.0)$/.test(l)))return;
 let name:string|undefined;const phones:string[]=[];
 for(const line of lines){
  const colon=line.indexOf(':');if(colon<0)continue;
  const header=line.slice(0,colon),field=header.split(';')[0].replace(/^[^.]+\./,'').toUpperCase();
  if(field!=='FN' && field!=='TEL')continue;
  if(/(?:ENCODING|CHARSET)=/i.test(header))return; // No guessed QP/base64 decoding.
  const value=line.slice(colon+1);
  if(field==='FN'){
   if(name!==undefined)return;
   name=plain(value.replace(/\\[nN]/g,' ').replace(/\\([,;\\])/g,'$1'),256)?.trim();
   if(!name)return;
  }else{
   const phone=value.replace(/^tel:/i,'').trim();
   if(!/^[+0-9 ()\-.]{3,80}$/.test(phone) || !/[0-9]/.test(phone))return;
   if(!phones.includes(phone))phones.push(phone);
   if(phones.length>8)return;
  }
 }
 name ||= plain(body.displayName,256)?.trim();
 if(!name || !phones.length)return;
 return [name,...phones].join('\n');
}

export function normalizeSharedContent(content:proto.IMessage):Projection|undefined {
 const contacts=content.contactMessage,collection=content.contactsArrayMessage;
 if(contacts || collection){
  if(contacts && collection)return contactFallback();
  const bodies=contacts?[contacts]:collection?.contacts;
  if(!Array.isArray(bodies) || !bodies.length || bodies.length>16)return contactFallback();
  const cards:string[]=[];
  for(let i=0;i<bodies.length;i++){
   const card=bodies[i] && contact(bodies[i]);if(!card)return contactFallback();cards.push(card);
  }
  const text=sharedText(['אנשי קשר — שם ומספרים בלבד',...cards].join('\n\n'));
  return text?{kind:'contact',text,raw:undefined}:contactFallback();
 }
 if(content.liveLocationMessage || content.locationMessage?.isLive)
  return fallback('live-location','שותף מיקום חי. מעקב ועדכוני מיקום אינם זמינים כאן; אפשר לפתוח ב־WhatsApp.');
 const location=content.locationMessage;
 if(!location)return;
 const lat=location.degreesLatitude,lon=location.degreesLongitude;
 // Own fields matter: protobuf's missing-coordinate defaults must not invent (0,0).
 if(!Object.hasOwn(location,'degreesLatitude') || !Object.hasOwn(location,'degreesLongitude') ||
   typeof lat!=='number' || typeof lon!=='number' || !Number.isFinite(lat) || !Number.isFinite(lon) || Math.abs(lat)>90 || Math.abs(lon)>180)
  return fallback('location-invalid','פרטי המיקום חסרים או אינם תקינים. אפשר לפתוח ב־WhatsApp.');
 const name=location.name==null?'':plain(location.name,256),address=location.address==null?'':plain(location.address,1024);
 if(name===undefined || address===undefined)return fallback('location-invalid','פרטי המיקום חורגים מגבולות התצוגה. אפשר לפתוח ב־WhatsApp.');
 return {kind:'location',text:['מיקום משותף',name,address,`${lat}, ${lon}`].filter(Boolean).join('\n'),raw:undefined};
}
