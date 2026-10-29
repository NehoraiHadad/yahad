import {readOnlyKind} from './rich-content.js';
import type { Connector, StoredMessage } from './connectors/types.js';

export function messageActions(message: StoredMessage,connector?: Connector) {
  const available=!!connector && !message.deleted && !message.restricted && message.kind!=='unsupported' && !readOnlyKind(message.kind) &&
    message.status!=='pending' && message.status!=='failed' &&
    (!connector.originalAvailable || connector.originalAvailable(message));
  return {reply:available && !!connector?.capabilities.reply,reactions:available && !!connector?.capabilities.reactions,
    edit:available && message.kind==='text' && !!connector?.capabilities.edit,delete:available && !!connector?.capabilities.delete};
}
// Node 24 is required by this project. Match one complete Unicode emoji sequence,
// including flags, modifiers and ZWJ forms, rather than arbitrary message text.
const emojiSequence=new RegExp('^\\p{RGI_Emoji}$','v');
export function validReaction(value: string) { return value==='' || value.length<=64 && emojiSequence.test(value); }
