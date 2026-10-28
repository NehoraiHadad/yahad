import { test } from 'node:test';
import assert from 'node:assert/strict';
import { visibleMessageIds } from '../web/useVisibleChatRead.js';

test('only actually intersecting rendered rows count; hidden/unfocused tab and hidden viewport never do', () => {
 const previous = globalThis.window;
 (globalThis as any).window = { innerWidth: 400, innerHeight: 600, getComputedStyle: () => ({ visibility: 'visible' }) };
 try {
   const row = (id: string, top: number, bottom: number, left=0, right=300) => ({
     dataset: { localUnreadId:id }, getBoundingClientRect: () => ({top,bottom,left,right,width:right-left,height:bottom-top})
   });
   const container: any = { closest: () => null, ownerDocument: { querySelectorAll: () => [] }, getClientRects: () => [1], getBoundingClientRect: () => ({top:100,bottom:500,left:0,right:350}),
     querySelectorAll: () => [row('above',0,90), row('visible',110,160), row('partially-visible',480,560), row('below',510,550), row('offscreen',150,170,410,450)] };
   assert.deepEqual(visibleMessageIds(container,true,true), ['visible','partially-visible']);
   assert.deepEqual(visibleMessageIds(container,false,true), []);
   assert.deepEqual(visibleMessageIds(container,true,false), []);
   container.closest = () => ({});
   assert.deepEqual(visibleMessageIds(container,true,true), []);
   container.closest = () => null;
   container.ownerDocument.querySelectorAll = () => [{getClientRects: () => [1]}];
   assert.deepEqual(visibleMessageIds(container,true,true), []);
   container.ownerDocument.querySelectorAll = () => [];
   container.getClientRects = () => [];
   assert.deepEqual(visibleMessageIds(container,true,true), []);
 } finally { (globalThis as any).window = previous; }
});

test('provider receipts retain existing capability but only follow visible local acknowledgement; cancellation blocks them', async () => {
 const { acknowledgeVisibleMessages } = await import('../web/useVisibleChatRead.js');
 const original = globalThis.fetch;
 const calls: {path: string; ids: string[]}[] = [];
 let active = true;
 (globalThis as any).fetch = async (path: string, init: RequestInit) => {
   calls.push({path, ids: JSON.parse(init.body as string).ids});
   return { ok:true, json: async () => ({ok:true}) };
 };
 try {
   await acknowledgeVisibleMessages('synthetic@g.us',['visible-only'],true,()=>active);
   assert.deepEqual(calls.map(c=>c.path), ['/api/groups/synthetic%40g.us/viewed','/api/groups/synthetic%40g.us/read']);
   assert.ok(calls.every(c=>c.ids.join() === 'visible-only'));
   calls.length=0;
   await acknowledgeVisibleMessages('synthetic@g.us',['a'],false,()=>true);
   assert.equal(calls.length,1);
   calls.length=0; active=false;
   await acknowledgeVisibleMessages('synthetic@g.us',['b'],true,()=>active);
   assert.equal(calls.length,1,'late local acknowledgement cannot send receipt after view closed');
 } finally { globalThis.fetch = original; }
});
