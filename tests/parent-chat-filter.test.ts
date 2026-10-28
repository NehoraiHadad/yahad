import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { filterParentGroups, searchChatGroups, parentEmptyMessage, ParentChatFilter, type ChatFilter } from '../web/ParentChatFilter.js';
import { CatalogConversation, ConversationVerification } from '../web/CatalogConversation.js';
import { LatestRequest, isGroupDataRefresh } from '../web/latestRequest.js';
import type { FamilyOverview } from '../web/FamilyProgress.js';

const groups = [{ id: 'one', title: 'Synthetic one' }, { id: 'two', title: 'Synthetic two' }, { id: 'parent-only', title: 'Synthetic parent' }];
const fixture = (): FamilyOverview => ({ state: 'connected', groups, children: [
  { id: 'a', name: 'עדי' }, { id: 'b', name: 'דנה' },
], grants: [{ user_id: 'a', group_id: 'one' }, { user_id: 'b', group_id: 'two' }] });
const byChild: ChatFilter = { mode: 'child', childId: 'a' };

test('parent filter intersects persisted child-ID grants with parent groups without altering inputs', () => {
  const data = fixture();
  data.grants.push({ user_id: 'a', group_id: 'missing' }, { user_id: 'a', group_id: 'one' });
  assert.deepEqual(filterParentGroups(groups, byChild, data, true), [groups[0]]);
  assert.deepEqual(filterParentGroups(groups, { mode: 'child', childId: 'b' }, data, true), [groups[1]]);
  assert.equal(groups.length, 3);
  assert.equal(data.grants.length, 4);
});

test('loading, reconnect, stale grants, removed child and no selection fail closed; all mode stays unchanged', () => {
  const data = fixture();
  for (const overview of [data, null]) {
    assert.deepEqual(filterParentGroups(groups, byChild, overview, false), []);
    assert.equal(filterParentGroups(groups, { mode: 'all', childId: 'a' }, overview, false), groups);
  }
  for (const childId of ['', 'deleted', 'parent']) {
    assert.deepEqual(filterParentGroups(groups, { mode: 'child', childId }, data, true), []);
  }
  data.children = data.children.filter(child => child.id !== 'a');
  assert.deepEqual(filterParentGroups(groups, byChild, data, true), []);
});

test('grant revocation immediately excludes the selected conversation, not just its list row', () => {
  const data = fixture();
  assert.equal(filterParentGroups(groups, byChild, data, true).find(group => group.id === 'one')?.id, 'one');
  data.grants = data.grants.filter(grant => grant.user_id !== 'a');
  assert.equal(filterParentGroups(groups, byChild, data, true).find(group => group.id === 'one'), undefined);
});

test('filter controls expose the unified account name, blank stale selection, and accurate parent identity explanation', () => {
  const html = renderToStaticMarkup(React.createElement(ParentChatFilter, {
    filter: { mode: 'child', childId: 'deleted' }, overview: fixture(), verified: false, onChange() {},
  }));
  assert.ok(html.includes('>עדי</option>'));
  assert.ok(html.includes('>דנה</option>'));
  assert.ok(!html.includes('example-a'), 'no separate login alias is rendered');
  assert.match(html, /<option value="" selected="">/);
  assert.ok(html.includes('נשארים בחשבון ההורה'));
  assert.ok(html.includes('ממתינים לאימות'));
});

test('all-mode copy describes the known group catalog rather than promising private conversations', () => {
  const html = renderToStaticMarkup(React.createElement(ParentChatFilter, {
    filter: { mode: 'all', childId: 'a' }, overview: fixture(), verified: true, onChange() {},
  }));
  assert.ok(html.includes('כל הקבוצות שסונכרנו'));
  assert.ok(html.includes('לא שיחות פרטיות'));
  assert.ok(html.includes('גם בקבוצות שלא אושרו לילדים'));
  assert.ok(html.includes('הרשאות הילדים אינן משתנות'));
  assert.equal(html.includes('<select'), false);
  assert.match(parentEmptyMessage({ mode: 'all', childId: '' }, fixture(), true), /ניהול ההורה/);
  const empty = fixture(); empty.children = [];
  assert.match(parentEmptyMessage(byChild, empty, true), /עדיין אין חשבונות ילדים/);
  assert.match(parentEmptyMessage({ mode: 'child', childId: 'removed' }, fixture(), true), /בחרו ילד/);
  assert.match(parentEmptyMessage(byChild, fixture(), false), /מאמתים הרשאות/);
});

test('normalized group search has explicit empty results and does not mutate the source or grants', () => {
  const data = fixture();
  assert.deepEqual(searchChatGroups(groups, '  SYNTHETIC PARENT  '), [groups[2]]);
  assert.deepEqual(searchChatGroups(groups, 'missing query'), []);
  assert.equal(searchChatGroups(groups, '  '), groups);
  assert.deepEqual(filterParentGroups(groups, byChild, data, true), [groups[0]]);
  assert.equal(groups.length, 3);
});

test('unavailable-content fallback stays closed and never invites accidental child access', () => {
  const html = renderToStaticMarkup(React.createElement(CatalogConversation, {
    title: 'Synthetic private group', onBack() {}, onRetry() {},
  }));
  assert.ok(html.includes('Synthetic private group'));
  assert.ok(html.includes('הגישה לשיחה אינה זמינה'));
  assert.ok(html.includes('ההודעות והשליחה מושהות'));
  assert.ok(html.includes('אין צורך לשנות הרשאות של ילד'));
  assert.ok(html.includes('לנסות שוב'));
  assert.ok(html.includes('חזרה לשיחות'));
  assert.doesNotMatch(html, /<textarea|<input|message-viewport|composer|history-panel/);
});

test('failed permission verification suspends content and exposes retry without implying revoked approval', () => {
  for (const checking of [true, false]) {
    const html = renderToStaticMarkup(React.createElement(ConversationVerification, {
      title: 'Synthetic group', checking, onBack() {}, onRetry() {},
    }));
    assert.ok(html.includes('ההודעות והשליחה מושהות'));
    assert.equal(html.includes('disabled=""'), checking);
    assert.doesNotMatch(html, /<textarea|<input|message-viewport|composer|history-panel/);
    assert.ok(!html.includes('אינה מאושרת'));
  }
});

test('chat wiring invalidates asynchronous responses and uses filtered groups for the active conversation', () => {
  // Wiring guard complements pure filtering tests; browser interaction requires separate verification.
  const source = readFileSync(new URL('../web/main.tsx', import.meta.url), 'utf8');
  const chat = source.slice(source.indexOf('function Chat('), source.indexOf('function Message'));
  assert.match(chat, /const current = visibleGroups\.find/);
  assert.match(chat, /const activeSelected = groupsVerified && visibleGroups\.some\(group => group\.id === selected && group\.canRead\)/);
  assert.match(chat, /\{current\?\.canRead && groupsVerified \? \(/);
  assert.match(chat, /\) : current && !groupsVerified \? \(\s*<ConversationVerification/);
  assert.match(chat, /groupsVerified && g\.canRead && g\.last/);
  assert.match(chat, /!groupsVerified\s*\? 'בודקים את הגישה לתוכן…'/);
  assert.match(chat, /\) : current \? \(\s*<CatalogConversation/);
  assert.match(chat, /!listBusy && !!visibleGroups\.length && !listedGroups\.length/);
  assert.match(chat, /me\.user\.role === 'parent' \? 'חשבון הורה · הסינון לא משנה הרשאות'/);
  assert.match(chat, /selectedRef\.current = activeSelected/);
  assert.match(chat, /if \(!listGeneration\.current\.isCurrent\(generation\)\) return/);
  assert.match(chat, /source\.onerror = \(\) => \{\s*setOnline\(false\);\s*listGeneration\.current\.invalidate\(\);\s*groupDataGeneration\.current\.invalidate\(\);\s*invalidateFilter\(\)/);
  assert.match(chat, /selectedRef\.current !== g \|\| generation !== requestGeneration\.current/);
  assert.match(chat, /me\.user\.role === 'parent' && <ParentChatFilter/);
  assert.match(source, /onClick=\{openChat\}/);
  assert.ok(!/disabled=\{[^}]*\}[^>]*onClick=\{openChat\}/.test(source));
});


test('async snapshots reject late grants after newer revocation, disconnect and cleanup', async () => {
  const gate = new LatestRequest();
  let verified = false;
  let snapshot: FamilyOverview | null = null;
  const deferred = () => {
    let resolve!: (data: FamilyOverview) => void;
    let reject!: (error: Error) => void;
    const promise = new Promise<FamilyOverview>((res, rej) => { resolve = res; reject = rej; });
    return { promise, resolve, reject };
  };
  const read = async (promise: Promise<FamilyOverview>) => {
    const request = gate.begin();
    verified = false;
    try {
      const data = await promise;
      if (!gate.isCurrent(request)) return;
      snapshot = data; verified = true;
    } catch {
      if (!gate.isCurrent(request)) return;
      verified = false;
    }
  };
  const old = deferred(), newer = deferred();
  const oldRead = read(old.promise), newerRead = read(newer.promise);
  const revoked = fixture(); revoked.grants = [];
  newer.resolve(revoked); await newerRead;
  old.resolve(fixture()); await oldRead;
  assert.deepEqual(filterParentGroups(groups, byChild, snapshot, verified), []);
  for (const reason of ['disconnect', 'unmount']) {
    const pending = deferred(), request = read(pending.promise);
    gate.invalidate(); verified = false;
    pending.resolve(fixture()); await request;
    assert.equal(verified, false, reason);
    assert.deepEqual(filterParentGroups(groups, byChild, snapshot, verified), [], reason);
  }
  const failed = deferred(), good = deferred();
  const failedRead = read(failed.promise), goodRead = read(good.promise);
  good.resolve(fixture()); await goodRead;
  failed.reject(new Error('synthetic late failure')); await failedRead;
  assert.equal(verified, true);
  assert.deepEqual(filterParentGroups(groups, byChild, snapshot, verified), [groups[0]]);
});


test('message-only refreshes preserve verified selection/draft lifetime; permission events fail closed', () => {
  const data = fixture();
  let verified = true;
  let mounts = 1;
  const draft = { text: 'Synthetic unsent draft', attachment: 'synthetic.png' };
  for (const payload of ['{"group":"one"}', '{"group":"two"}']) {
    if (!isGroupDataRefresh(payload)) { verified = false; mounts++; }
    assert.equal(filterParentGroups(groups, byChild, data, verified)[0].id, 'one');
    assert.equal(mounts, 1);
    assert.equal(draft.text, 'Synthetic unsent draft');
  }
  for (const payload of ['{}', 'null', '[]', '{"group":null}', '{"group":""}', 'broken']) {
    assert.equal(isGroupDataRefresh(payload), false);
  }
  const service = readFileSync(new URL('../src/service.ts', import.meta.url), 'utf8');
  const grant = service.slice(service.indexOf('async grant('), service.indexOf('async grant(') + 600);
  assert.match(grant, /this\.notify\(f, g, 'revoked'\)/);
  const server = readFileSync(new URL('../src/server.ts', import.meta.url), 'utf8');
  assert.match(server, /type === 'revoked' \|\| type === 'reset' \|\| type === 'session'\) \{\s*emit\('refresh', \{\}\);/);
  const source = readFileSync(new URL('../web/main.tsx', import.meta.url), 'utf8');
  assert.match(source, /if \(isGroupDataRefresh\(\(event as MessageEvent\)\.data\)\) void refreshGroupData\(\);\s*else void refreshList\(\);/);
  const messageRefresh = source.slice(source.indexOf('const refreshGroupData'), source.indexOf('const refreshMessages'));
  assert.ok(!messageRefresh.includes('invalidateFilter'));
  assert.ok(!messageRefresh.includes('setSelected'));
  assert.ok(!messageRefresh.includes('setFilterVerified'));
});
