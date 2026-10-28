import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import { filterParentGroups, searchChatGroups } from '../web/ParentChatFilter.js';
import { LatestRequest } from '../web/latestRequest.js';

// Execute the actual Chat hooks/handlers with synthetic deferred API responses.
// This verifies state/race behavior; it is not a browser layout or focus test.
function harness() {
  const source = readFileSync(new URL('../web/main.tsx', import.meta.url), 'utf8');
  const start = source.indexOf('function Chat('), end = source.indexOf('  return (\n    <main className={`chat-app', start);
  assert.ok(start >= 0 && end > start);
  const code = ts.transpileModule(`${source.slice(start, end)}return {
    visibleGroups, listedGroups, activeSelected, current, selectedRef, requestGeneration,
    chatFilter, query, changeFilter, refreshList, refreshGroupData, refreshMessages,
    setSelected, setQuery, setGroups, setFilterOverview, setFilterVerified, setMessages, messages,
    setGroupsVerified, groupsVerified, groupsChecking
  }; }; Chat;`, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
  const slots: any[] = [], effects: (() => void)[] = [], requests: { path: string; resolve: (value: any) => void; reject: (error: Error) => void }[] = [];
  let cursor = 0;
  const Chat = vm.runInNewContext(code, {
    useState(value: any) {
      const index = cursor++;
      if (!(index in slots)) slots[index] = typeof value === 'function' ? value() : value;
      return [slots[index], (next: any) => { slots[index] = typeof next === 'function' ? next(slots[index]) : next; }];
    },
    useRef(value: any) { const index = cursor++; return slots[index] ||= { current: value }; },
    useCallback(fn: any) { return fn; },
    useEffect(fn: () => void, deps: any[]) {
      const index = cursor++, previous = slots[index];
      slots[index] = deps;
      // The SSE mount and theme persistence are unrelated to this local state harness.
      if (fn.toString().includes('new EventSource') || fn.toString().includes('localStorage')) return;
      if (!previous || deps.some((dep, i) => !Object.is(dep, previous[i]))) effects.push(fn);
    },
    api: (path: string) => new Promise((resolve, reject) => requests.push({ path, resolve, reject })),
    localStorage: { getItem() { return null; }, setItem() {} },
    filterParentGroups, searchChatGroups, LatestRequest, setTimeout() {}, clearTimeout() {},
    document: { visibilityState: 'hidden' },
  });
  const render = () => { cursor = 0; return Chat({ me: { user: { id: 'parent', role: 'parent' }, capabilities: {} }, onError() {}, onMe() {} }); };
  const flush = () => { let value = render(); while (effects.length) { effects.splice(0).forEach(fn => fn()); value = render(); } return value; };
  return { render, flush, requests };
}
const tick = () => new Promise<void>(resolve => setImmediate(resolve));
const knownGroups = [{ id: 'approved', title: 'Synthetic approved', canRead: true }, { id: 'catalog', title: 'Synthetic catalog', canRead: false }];

test('revocation closes content immediately and rejects late messages; new approval reopens through a fresh read', async () => {
  const h = harness();
  let state = h.flush();
  state.setGroups(knownGroups); state.setGroupsVerified(true); state.setSelected('approved');
  state = h.flush();
  assert.equal(state.activeSelected, 'approved');
  assert.equal(h.requests.length, 1);
  const oldGeneration = state.requestGeneration.current;
  state.setMessages([{ id: 'old-visible-message' }]);
  state.setGroups(knownGroups.map(g => ({ ...g, canRead: false })));
  state = h.render();
  assert.equal(state.current.id, 'approved', 'catalog details remain selected');
  assert.equal(state.current.canRead, false);
  assert.equal(state.activeSelected, null);
  assert.equal(state.selectedRef.current, null, 'response guard closes during render, before effects');
  state = h.flush();
  assert.ok(state.requestGeneration.current > oldGeneration);
  assert.equal(state.messages.length, 0);
  h.requests[0].resolve({ messages: [{ id: 'late', time: 1 }], cursor: null });
  await tick();
  assert.equal(h.flush().messages.length, 0, 'old content must not return after revocation');
  state.setGroups(knownGroups);
  state = h.flush();
  assert.equal(state.activeSelected, 'approved');
  assert.equal(h.requests.length, 2, 'new permission requires new content request');
  h.requests[1].resolve({ messages: [{ id: 'new', time: 2 }], cursor: null });
  await tick();
  assert.equal(h.flush().messages[0].id, 'new');
});

test('catalog selection never requests content; All resets child search and repeated clicks are inert', () => {
  const h = harness();
  let state = h.flush();
  state.setGroups(knownGroups); state.setGroupsVerified(true); state.setSelected('catalog');
  state = h.flush();
  assert.equal(state.current.id, 'catalog');
  assert.equal(state.activeSelected, null);
  assert.equal(h.requests.length, 0);
  state.changeFilter({ mode: 'child', childId: 'child-a' });
  state = h.flush();
  assert.equal(h.requests.length, 2, 'entering child mode validates groups and grants');
  state.setQuery('old child search');
  state = h.flush();
  state.changeFilter({ mode: 'all', childId: 'child-a' });
  state = h.flush();
  assert.equal(state.query, '');
  assert.equal(state.current, undefined);
  assert.equal(state.listedGroups.length, 2);
  const generation = state.requestGeneration.current;
  state.changeFilter({ mode: 'all', childId: 'child-a' });
  state = h.flush();
  assert.equal(state.requestGeneration.current, generation);
  assert.equal(h.requests.length, 2, 'repeated All never starts duplicate work');
});

test('failed permission refresh stays closed in All mode and late message-only snapshots cannot reopen it', async () => {
  const h = harness();
  let state = h.flush();
  state.setGroups(knownGroups); state.setGroupsVerified(true); state.setSelected('approved');
  state = h.flush();
  state.setMessages([{ id: 'old-visible' }]);
  const oldGroupRead = state.refreshGroupData();
  assert.equal(h.requests[1].path, '/groups');
  const permissionRead = state.refreshList();
  state = h.flush();
  assert.equal(state.groupsVerified, false);
  assert.equal(state.groupsChecking, true);
  assert.equal(state.selectedRef.current, null);
  assert.equal(state.messages.length, 0);
  assert.equal(state.visibleGroups.length, 2, 'catalog names remain visible while content is suspended');
  await state.refreshGroupData();
  assert.equal(h.requests.length, 4, 'message events cannot overtake the pending full snapshot');
  h.requests[0].resolve({ messages: [{ id: 'late-content', time: 1 }], cursor: null });
  h.requests[1].resolve(knownGroups); await oldGroupRead;
  h.requests[2].reject(new Error('Synthetic offline failure'));
  h.requests[3].resolve({ children: [], grants: [], groups: [] });
  await permissionRead; await tick();
  state = h.flush();
  assert.equal(state.groupsVerified, false);
  assert.equal(state.groupsChecking, false);
  assert.equal(state.current.canRead, true, 'old metadata can remain, but not authorize content');
  assert.equal(state.activeSelected, null);
  assert.equal(state.messages.length, 0);
  await state.refreshGroupData();
  assert.equal(h.requests.length, 4, 'message events cannot clear failed permission state');

  const retry = state.refreshList();
  h.requests[4].resolve(knownGroups.map(group => ({ ...group, canRead: false })));
  h.requests[5].resolve({ children: [], grants: [], groups: [] });
  await retry;
  state = h.flush();
  assert.equal(state.groupsVerified, true);
  assert.equal(state.current.canRead, false);
  assert.equal(state.activeSelected, null);
  assert.equal(state.messages.length, 0);
});

test('only newest full snapshot restores content; ordinary messages do not reset verified conversation state', async () => {
  const h = harness();
  let state = h.flush();
  const oldRead = state.refreshList(), newRead = state.refreshList();
  h.requests[2].resolve(knownGroups);
  h.requests[3].resolve({ children: [], grants: [], groups: [] });
  await newRead;
  h.requests[0].resolve([]);
  h.requests[1].resolve({ children: [], grants: [], groups: [] });
  await oldRead;
  state = h.flush();
  assert.equal(state.groupsVerified, true);
  assert.equal(state.visibleGroups.length, 2);
  state.setSelected('approved');
  state = h.flush();
  h.requests[4].resolve({ messages: [{ id: 'saved', time: 1 }], cursor: null });
  await tick();
  state = h.flush();
  const generation = state.requestGeneration.current;
  const messageUpdate = state.refreshGroupData();
  state = h.flush();
  assert.equal(state.groupsVerified, true);
  assert.equal(state.activeSelected, 'approved');
  h.requests[5].resolve(knownGroups);
  await messageUpdate;
  state = h.flush();
  assert.equal(state.requestGeneration.current, generation, 'no content/composer lifecycle reset for message updates');
  assert.equal(state.messages[0].id, 'saved');
  assert.equal(h.requests.length, 6, 'message-only list update does not request history or permissions again');
});

test('removed catalog selection stays cleared when the group reappears later', async () => {
  const h = harness();
  let state = h.flush();
  state.setGroups(knownGroups); state.setGroupsVerified(true); state.setSelected('catalog');
  state = h.flush();
  assert.equal(state.current.id, 'catalog');
  const removed = state.refreshList();
  h.requests[0].resolve([knownGroups[0]]);
  h.requests[1].resolve({ children: [], grants: [], groups: [] });
  await removed;
  state = h.flush();
  assert.equal(state.current, undefined);
  const restored = state.refreshList();
  h.requests[2].resolve(knownGroups);
  h.requests[3].resolve({ children: [], grants: [], groups: [] });
  await restored;
  state = h.flush();
  assert.equal(state.visibleGroups.length, 2);
  assert.equal(state.current, undefined, 'reappearing catalog entries do not silently reopen');
});
