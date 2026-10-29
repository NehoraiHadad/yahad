/** Internal boundary for a future verified sender. Client-supplied keys/JIDs
 * are intentionally absent. No endpoint or vote control activates this. */
export interface PollVoteIntent {conversationId:string;pollId:string;optionIndexes:readonly number[]}
export interface PollVoteSending {readonly available:boolean;send(intent:PollVoteIntent):Promise<void>}
export const unavailablePollVoting:PollVoteSending=Object.freeze({
 available:false,
 async send(_intent:PollVoteIntent):Promise<never>{
  throw new Error('Poll voting is unavailable: upstream send path requires independent interoperability verification');
 },
});
