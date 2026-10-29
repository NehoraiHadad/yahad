import {test} from 'node:test';
import assert from 'node:assert/strict';
import {fixture} from './helpers/message-fixture.js';
import {poll,vote} from './helpers/poll-fixture.js';
import {SendNotSubmittedError} from '../src/send-outcome.js';

// Cross-branch regressions: the timer's private send metadata and received poll
// state must stay private together, and its preparation wrapper must retain the
// receive-only kind guards added by the rich-content branch.
test('combined projection strips private poll context and send identity fields while retaining partial counts',async()=>{
 const f=await fixture();try{
  await f.ingest(poll());
  await f.emit('messages.upsert',{type:'notify',messages:[vote('observed',['First'])]});
  f.store.putMessage(f.a.familyId,{...f.get('poll')!,sendRequestHash:'PRIVATE_REQUEST_HASH',sendAcknowledged:true},false);
  const response=await f.request('/messages');assert.equal(response.statusCode,200);
  const message=response.json().messages.find((m:any)=>m.id==='poll');
  assert.deepEqual(message.pollResults,{counts:[1,0],partial:true});
  for(const field of ['pollState','pollContext','sendRequestHash','sendAcknowledged','raw'])
   assert.equal(Object.hasOwn(message,field),false,field);
  assert.ok(!response.body.includes('PRIVATE_REQUEST_HASH'));
 }finally{await f.close();}
});

test('combined preparation proves receive-only and unknown kinds never reach provider handoff',async()=>{
 const f=await fixture();let handoffs=0;try{
  for(const kind of ['poll','contact','location','interactive','poll_vote']){
   await assert.rejects(f.connector.send({id:'blocked-'+kind,groupId:'120363001@g.us',kind,text:'Synthetic only',
    beforeSend:()=>{handoffs++;}} as any),SendNotSubmittedError);
  }
  assert.equal(handoffs,0);assert.equal(f.sent.length,0);
 }finally{await f.close();}
});
