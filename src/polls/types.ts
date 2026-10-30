/** Internal only. Persisted in a separate table sealed with the conversation key;
 * never spread into an API response, notification or log. */
export interface PollContext {secret?:string;creators:string[];selectable:number}
export interface EncryptedPollVote {target:string;id:string;time:number;voters:string[];payload:string;iv:string}
export interface PollBallot {aliases:string[];time:number;id:string;selected:number[]}
export interface PollState extends PollContext {fingerprint:string;ballots:PollBallot[];limited?:boolean}
export interface ObservedPollResults {counts:number[];partial:true}
