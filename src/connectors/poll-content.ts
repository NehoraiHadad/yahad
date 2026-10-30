import type {proto} from '@whiskeysockets/baileys';
import type {Incoming} from './types.js';
import {pollDisplay,invalidPollText,pollDisplayLimits} from '../poll-display.js';

type Projection=Pick<Incoming,'kind'|'text'|'poll'|'unsupportedReason'|'raw'>;
const fallback=(reason:Incoming['unsupportedReason'],text:string):Projection=>({kind:'unsupported',text,unsupportedReason:reason,raw:undefined});

export function normalizePoll(content:proto.IMessage):Projection|undefined {
  if(content.pollCreationMessageV4 || content.pollCreationMessageV5)
    return fallback('poll-variant','סקר בפורמט חדש שעדיין אינו מוצג כאן. אפשר לפתוח אותו ב־WhatsApp.');
  if(content.pollCreationOptionImageMessage)
    return fallback('poll-variant','אפשרות בסקר עם תמונה אינה מוצגת כאן. אפשר לפתוח אותה ב־WhatsApp.');
  if(content.pollUpdateMessage)return fallback('poll-vote','עדכון הצבעה בסקר — פרטי ההצבעה זמינים כרגע ב־WhatsApp בלבד');
  if(content.pollResultSnapshotMessage || content.pollResultSnapshotMessageV3)
    return fallback('poll-results','סיכום תוצאות סקר אינו מוצג כאן. אפשר לפתוח אותו ב־WhatsApp.');
  const bodies=[content.pollCreationMessage,content.pollCreationMessageV2,content.pollCreationMessageV3].filter(Boolean);
  if(!bodies.length)return;
  if(bodies.length!==1)return fallback('poll-invalid',invalidPollText);
  const body=bodies[0]!;
  if(body.pollType!=null && body.pollType!==0 || body.correctAnswer!=null)
    return fallback('poll-variant','סקר חידון או סוג סקר שלא אומת אינו מוצג כאן. אפשר לפתוח אותו ב־WhatsApp.');
  if(body.pollContentType!=null && body.pollContentType!==0 && body.pollContentType!==1)
    return fallback('poll-variant','סקר עם תוכן שאינו טקסט אינו מוצג כאן. אפשר לפתוח אותו ב־WhatsApp.');
  if(!Array.isArray(body.options) || body.options.length>pollDisplayLimits.options)
    return fallback('poll-invalid',invalidPollText);
  const display=pollDisplay(body.name,body.options.map(o=>o?.optionName));
  const count=body.selectableOptionsCount;
  if(!display || count!=null && (!Number.isInteger(count) || count<0 || count>body.options.length))
    return fallback('poll-invalid',invalidPollText);
  return {kind:'poll',...display,raw:undefined};
}
