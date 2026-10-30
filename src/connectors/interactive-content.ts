import type {proto} from '@whiskeysockets/baileys';
import type {Incoming} from './types.js';
import {sharedText} from '../rich-content.js';
type Projection=Pick<Incoming,'kind'|'text'|'raw'|'unsupportedReason'>;
const notice='תצוגת טקסט בלבד. הכפתורים והפעולות אינם זמינים כאן.';
const fallback=():Projection=>({kind:'unsupported',raw:undefined,unsupportedReason:'interactive-variant',text:'הודעה אינטראקטיבית בפורמט שאינו מוצג כאן. אפשר לפתוח אותה ב־WhatsApp.'});
const optional=(x:unknown,max:number)=>x==null?'':typeof x==='string'&&x.length<=max?x:undefined;
const media=(b:any)=>!!b&&(b.hasMediaAttachment||b.documentMessage||b.imageMessage||b.videoMessage||b.audioMessage||b.locationMessage||b.productMessage||b.jpegThumbnail?.length);

/** Text-only projection. Never parse or execute native-flow JSON, retain command
 * IDs, follow CTA URLs/phone numbers, or import nested media/previews. */
export function normalizeInteractive(content:proto.IMessage):Projection|undefined{
 const fields=['buttonsMessage','buttonsResponseMessage','templateButtonReplyMessage','listMessage','listResponseMessage','templateMessage','interactiveMessage','interactiveResponseMessage'] as const;
 const present=fields.filter(k=>!!content[k]);if(!present.length)return;
 if(present.length!==1)return fallback();
 const field=present[0],b:any=content[field],parts:unknown[]=[];
 const title=(x:unknown)=>optional(x,256),body=(x:unknown)=>optional(x,4096),footer=(x:unknown)=>optional(x,512);
 if(field==='buttonsResponseMessage'||field==='templateButtonReplyMessage'){
  parts.push('תגובה לכפתור',body(b.selectedDisplayText));
 }else if(field==='listResponseMessage'){
  parts.push('תגובה לרשימה',title(b.title),body(b.description));
 }else if(field==='interactiveResponseMessage'){
  if(b.body?.format!=null && b.body.format!==0)return fallback();
  parts.push('תגובה אינטראקטיבית',body(b.body?.text));
 }else if(field==='buttonsMessage'){
  if(media(b)||!Array.isArray(b.buttons)||b.buttons.length>10)return fallback();
  parts.push(title(b.text),body(b.contentText),footer(b.footerText));
  for(const button of b.buttons){
   if(!button||button.nativeFlowInfo||button.type!=null&&button.type!==0&&button.type!==1)return fallback();
   const label=title(button.buttonText?.displayText);if(!label?.trim())return fallback();parts.push(label);
  }
 }else if(field==='listMessage'){
  if(b.productListInfo||b.listType!=null&&b.listType!==0&&b.listType!==1||!Array.isArray(b.sections)||b.sections.length>10)return fallback();
  parts.push(title(b.title),body(b.description),footer(b.footerText),title(b.buttonText));let rows=0;
  for(const section of b.sections){
   if(!section||!Array.isArray(section.rows))return fallback();parts.push(title(section.title));
   for(const row of section.rows){if(!row||++rows>32)return fallback();const label=title(row.title);if(!label?.trim())return fallback();parts.push(label,footer(row.description));}
  }
 }else if(field==='templateMessage'){
  const variants=[b.hydratedTemplate,b.hydratedFourRowTemplate].filter(Boolean);
  if(variants.length!==1||b.fourRowTemplate||b.interactiveMessageTemplate)return fallback();
  const t=variants[0];if(t.maskLinkedDevices||media(t)||!Array.isArray(t.hydratedButtons)||t.hydratedButtons.length>10)return fallback();
  parts.push(title(t.hydratedTitleText),body(t.hydratedContentText),footer(t.hydratedFooterText));
  for(const button of t.hydratedButtons){
   const variants=button&&[button.quickReplyButton,button.urlButton,button.callButton].filter(Boolean);
   if(!variants||variants.length!==1)return fallback();
   const label=title(variants[0].displayText);if(!label?.trim())return fallback();parts.push(label);
  }
 }else{
  if(media(b.header)||media(b.footer)||b.carouselMessage||b.collectionMessage||b.shopStorefrontMessage)return fallback();
  parts.push(title(b.header?.title),title(b.header?.subtitle),body(b.body?.text),footer(b.footer?.text));
 }
 if(parts.some(p=>p===undefined))return fallback();
 // A response label alone must not invent absent selected text.
 const visible=parts.filter((p):p is string=>typeof p==='string'&&!!p.trim());
 if(!visible.length||field.endsWith('ResponseMessage')&&visible.length<2||field==='templateButtonReplyMessage'&&visible.length<2)return fallback();
 const text=sharedText([...visible,notice].join('\n'));return text?{kind:'interactive',text,raw:undefined}:fallback();
}
