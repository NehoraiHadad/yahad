import {test} from 'node:test';
import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import {raw,fixture,group} from './helpers/message-fixture.js';

const outgoingId=(clientId:string)=>'FC'+clientId.replaceAll('-','').toUpperCase();
const settle=async()=>{for(let i=0;i<4;i++)await new Promise<void>(r=>setImmediate(r));};
const receipt=(id:string,userJid:string,kind:'receiptTimestamp'|'readTimestamp',fromMe=true)=>
  ({key:{id,remoteJid:group,fromMe,participant:userJid},receipt:{userJid,[kind]:Math.floor(Date.now()/1000)}});

async function sendText(f:Awaited<ReturnType<typeof fixture>>){
  const clientId=randomUUID(),response=await f.request('/messages','POST',{clientId,text:'Hello'});
  assert.equal(response.statusCode,200);return outgoingId(clientId);
}

test('a sent message leaves pending only on the server ack, which also enables edit and delete',async()=>{
  const f=await fixture();try{
    const id=await sendText(f);
    assert.equal(f.get(id)!.status,'pending');
    assert.deepEqual(f.service.public(f.a.familyId,f.get(id)!).actions,{reply:false,reactions:false,edit:false,delete:false});
    await f.ack(id,{error:'479'});assert.equal(f.get(id)!.status,'pending');
    await f.ack('UNRELATED');assert.equal(f.get('UNRELATED'),undefined);
    await f.ack(id);
    const m=f.get(id)!;assert.equal(m.status,'sent');
    assert.deepEqual(f.service.public(f.a.familyId,m).actions,{reply:true,reactions:true,edit:true,delete:true});
    const deleted=await f.request('/messages/'+id,'DELETE');assert.equal(deleted.statusCode,200);
    assert.equal(f.get(id)!.deleted,true);assert.deepEqual(f.sent.at(-1).content.delete.id,id);
  }finally{await f.close();}
});

test('group ticks follow WhatsApp semantics: all recipients delivered, then all recipients read',async()=>{
  const f=await fixture();try{
    const id=await sendText(f);await f.ack(id);
    // Our own device's "sender" receipt is reported as read by us; it is never a recipient.
    await f.emit('message-receipt.update',[receipt(id,'999@s.whatsapp.net','readTimestamp')]);await settle();
    assert.equal(f.get(id)!.status,'sent');assert.equal(f.get(id)!.groupReceipts,undefined);
    await f.emit('message-receipt.update',[receipt(id,'100@lid','receiptTimestamp'),receipt(id,'300@lid','readTimestamp')]);await settle();
    assert.equal(f.get(id)!.status,'sent');
    assert.deepEqual(f.get(id)!.groupReceipts,{'100@lid':'delivered','200@lid':'pending'});
    // Phone-number JIDs resolve to the same participant as their LID.
    await f.emit('message-receipt.update',[receipt(id,'200@s.whatsapp.net','receiptTimestamp')]);await settle();
    assert.equal(f.get(id)!.status,'delivered');
    await f.emit('message-receipt.update',[receipt(id,'100@lid','readTimestamp')]);await settle();
    assert.equal(f.get(id)!.status,'delivered');
    await f.emit('message-receipt.update',[receipt(id,'200@lid','readTimestamp'),receipt(id,'200@lid','receiptTimestamp')]);await settle();
    const m=f.get(id)!;assert.equal(m.status,'read');assert.deepEqual(m.groupReceipts,{'100@lid':'read','200@lid':'read'});
    assert.equal('groupReceipts' in f.service.public(f.a.familyId,m),false);
    assert.equal(f.metadataCalls.length,1);
    // A later provider echo of the same message keeps the receipts and the status.
    await f.ingest(raw(id,{conversation:'Hello'},{key:{id,remoteJid:group,fromMe:true},status:1}));
    assert.equal(f.get(id)!.status,'read');assert.deepEqual(f.get(id)!.groupReceipts,m.groupReceipts);
  }finally{await f.close();}
});

test('a read receipt before the ack still settles the ticks; receipts for others\' messages are ignored',async()=>{
  const f=await fixture();try{
    const id=await sendText(f);
    await f.emit('message-receipt.update',[receipt(id,'100@lid','readTimestamp'),receipt(id,'200@lid','readTimestamp')]);await settle();
    assert.equal(f.get(id)!.status,'read');
    await f.ack(id);assert.equal(f.get(id)!.status,'read');
    await f.ingest(raw('incoming',{conversation:'Hi'}));
    await f.emit('message-receipt.update',[receipt('incoming','200@lid','readTimestamp',false)]);await settle();
    assert.equal(f.get('incoming')!.groupReceipts,undefined);assert.equal(f.metadataCalls.length,1);
  }finally{await f.close();}
});
