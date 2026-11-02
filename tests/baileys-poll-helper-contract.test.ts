/** Synthetic upstream-helper regression contract. Never imported by production code.
 * Synthetic ciphertext is built using the pinned official crypto primitives and
 * the context derivation published in decryptPollVote / PR #2870. This validates
 * helper contracts only, not WhatsApp wire interoperability or a vote sender. */
import {test} from 'node:test';
import assert from 'node:assert/strict';
import {decryptPollVote,aesEncryptGCM,hmacSign,sha256,proto,
 getAggregateVotesInPollMessage,updateMessageWithPollUpdate,jidNormalizedUser} from '@whiskeysockets/baileys';

const pollMsgId='SYNTHETIC-POLL',pollEncKey=Buffer.alloc(32,7),encIv=Buffer.alloc(12,3);
const hash=(name:string)=>sha256(Buffer.from(name));
function fixture(creator:string,voter:string,options:string[]){
 const context={pollMsgId,pollEncKey,pollCreatorJid:creator,voterJid:voter};
 const sign=Buffer.concat([Buffer.from(pollMsgId),Buffer.from(creator),Buffer.from(voter),Buffer.from('Poll Vote'),Buffer.from([1])]);
 const key=hmacSign(sign,hmacSign(pollEncKey,Buffer.alloc(32),'sha256'),'sha256');
 const bytes=proto.Message.PollVoteMessage.encode({selectedOptions:options.map(hash)}).finish();
 return {context,vote:{encIv,encPayload:aesEncryptGCM(bytes,key,encIv,Buffer.from(`${pollMsgId}\0${voter}`))}};
}
const options=['First','Second'];
const poll=()=>({message:{pollCreationMessageV3:{name:'Synthetic',options:options.map(optionName=>({optionName}))}},pollUpdates:[]}as any);
const update=(name:string,time:number,id='vote',participant='100@lid')=>({pollUpdateMessageKey:{id,remoteJid:'synthetic@g.us',participant,fromMe:false},senderTimestampMs:time,vote:{selectedOptions:name?[hash(name)]:[]}});
const counts=(m:any)=>getAggregateVotesInPollMessage(m).map(x=>({name:x.name,count:x.voters.length}));

test('exported official helper authenticates explicit PN and LID context pairs, not a guessed universal mode',()=>{
 for(const creator of ['101@s.whatsapp.net','101@lid'])for(const voter of ['202@s.whatsapp.net','202@lid']){
  const {vote,context}=fixture(creator,voter,options);
  assert.deepEqual(decryptPollVote(vote,context).selectedOptions.map(x=>Buffer.from(x).toString('hex')),options.map(x=>hash(x).toString('hex')));
  const otherVoter=voter.includes('@lid')?'202@s.whatsapp.net':'202@lid';
  const otherCreator=creator.includes('@lid')?'101@s.whatsapp.net':'101@lid';
  assert.throws(()=>decryptPollVote(vote,{...context,voterJid:otherVoter}));assert.throws(()=>decryptPollVote(vote,{...context,pollCreatorJid:otherCreator}));
 }
 assert.equal(jidNormalizedUser('101:77@lid'),'101@lid');
});

test('wrong key, poll ID, identity, device suffix or tampered ciphertext must not become a vote',()=>{
 const {vote,context}=fixture('101@lid','202@lid',['First']);
 for(const patch of [{pollEncKey:Buffer.alloc(32,8)},{pollMsgId:'OTHER-POLL'},{pollCreatorJid:'101:77@lid'},{voterJid:'202:77@lid'}])assert.throws(()=>decryptPollVote(vote,{...context,...patch}));
 const bad=Buffer.from(vote.encPayload);bad[0]^=1;assert.throws(()=>decryptPollVote({...vote,encPayload:bad},context));
 assert.throws(()=>decryptPollVote({...vote,encIv:Buffer.alloc(12,4)},context));
});

test('official helper decodes changed/multiple selections and withdrawal, aggregator expects already decrypted updates',()=>{
 const m=poll();
 for(const selected of [['First'],['Second'],options,[]]){
  const {vote,context}=fixture('101@lid','202@lid',selected),decoded=decryptPollVote(vote,context);
  updateMessageWithPollUpdate(m,{...update('',100),vote:decoded});
  assert.deepEqual(counts(m),options.map(name=>({name,count:selected.includes(name)?1:0})));
 }
});

test('upstream update helper has no stale-event guard and drops withdrawal tombstones: receiver must add both',()=>{
 const m=poll();updateMessageWithPollUpdate(m,update('First',200));updateMessageWithPollUpdate(m,update('',300));
 assert.equal(m.pollUpdates.length,0);
 updateMessageWithPollUpdate(m,update('First',100));assert.equal(counts(m)[0].count,1); // observed limitation, not desired product behavior
 updateMessageWithPollUpdate(m,update('Second',500));updateMessageWithPollUpdate(m,update('First',400));
 assert.equal(counts(m)[0].count,1);assert.equal(counts(m)[1].count,0);
});

test('aggregation does not establish alias equivalence or option validity: caller needs authenticated evidence and validation',()=>{
 const m=poll();updateMessageWithPollUpdate(m,update('First',100,'a','202@lid'));
 updateMessageWithPollUpdate(m,update('First',200,'b','202@s.whatsapp.net'));
 assert.equal(counts(m)[0].count,2); // no PN/LID merge in this helper
 updateMessageWithPollUpdate(m,update('Unrecognized option',300,'c','303@lid'));
 assert.ok(counts(m).some(x=>x.name==='Unknown'));
});
