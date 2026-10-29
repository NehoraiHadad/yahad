import type { PollOptions } from './connectors/types.js';

// Product display bounds, not a claim about every WhatsApp poll variant.
export const pollDisplayLimits={question:2048,option:512,options:32} as const;
export const pollUnavailable='הצבעה ותוצאות אינן זמינות כאן.';
export const invalidPollText='לא ניתן להציג את פרטי הסקר: השאלה או האפשרויות חסרות או חורגות מהמגבלות.';

/** Shared allowlist for persistence, public projection and bounded rendering.
 * Copy only plain option text; never keys, answers, context or vote counts. */
export function pollDisplay(question: unknown,options: unknown): {text:string;poll:PollOptions} | undefined {
  if(typeof question!=='string' || !question.trim() || question.length>pollDisplayLimits.question ||
    !Array.isArray(options) || options.length<2 || options.length>pollDisplayLimits.options)return;
  const plain:string[]=[];
  for(let i=0;i<options.length;i++){
    const option=options[i];
    if(typeof option!=='string' || !option.trim() || option.length>pollDisplayLimits.option)return;
    plain.push(option);
  }
  return {text:question,poll:{options:plain}};
}
