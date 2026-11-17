import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { modeRequestErrorWords, updateCodePattern, updateErrorWords, updateStatus } from '../src/updates.js';
import type { WindowsUpdateTransport } from '../src/windows-update-transport.js';
import type { Config } from '../src/config.js';
import type { User } from '../src/store.js';
import { describeUpdateFailure, modeRequestErrorTexts, resolveUpdateCode, unavailableStages, unavailableUpdatesText, updateCodePrefixes, updateCodes,
  updateErrorTexts, updateRejectionFallback, updateRejectionTexts } from '../web/updateErrors.js';
import { updateProgress, type ConfiguredUpdate } from '../web/updateProgress.js';
import { RejectedUpdateRequest, submitUpdateRequest, UpdateProgressStatus } from '../web/UpdatesPanel.js';
import { setCsrf } from '../web/api.js';

const read = (path: string) => readFileSync(new URL('../' + path, import.meta.url), 'utf8');
const literals = (source: string) => new Set([...source.matchAll(/"([A-Z][A-Z0-9_]{2,63})"/g)].map(match => match[1]));

// Uppercase literals in the Updater that never reach the status as a failure code, each for its reason.
const notStatusCodes: Record<string, string> = {
  BAD_REQUEST: 'pipe reply to a malformed request', BUSY: 'pipe reply: an operation is running', INTERNAL: 'pipe reply',
  NOT_AVAILABLE: 'pipe reply: no release key', STALE_TARGET: 'pipe reply: install target changed',
  UPGRADED: 'the Setup result word for success', UPDATER_PEER: 'pipe client identification', UPDATER_REVERT: 'pipe impersonation revert',
  UPDATER_IMAGE_LOCATION: 'service start check, reported as event 1001', YAHAD_SCM_DISPATCH_REFUSED: 'process stderr on a refused start',
  YAHAD_SERVICE_ARGUMENTS_REFUSED: 'process stderr on a refused start', RELEASE_KEY_EMBEDDED_INVALID: 'build-time key check, never at runtime',
};

test('every word the status accepts has its own deliberate text', () => {
  assert.deepEqual(Object.keys(updateErrorTexts).sort(), [...updateErrorWords, 'expired'].sort());
  assert.deepEqual(Object.keys(modeRequestErrorTexts).sort(), [...modeRequestErrorWords].sort());
  for (const [word, text] of Object.entries(updateErrorTexts)) {
    assert.ok(text.title && text.what && text.todo, word);
    assert.ok(['user_action', 'transient', 'environment', 'defect'].includes(text.category), word);
  }
});

test('every code the Windows Updater can record resolves to a deliberate catalog entry', () => {
  const directory = new URL('../installer/Yahad.Updater/', import.meta.url);
  const sources = readdirSync(directory).filter(name => name.endsWith('.cs')).map(name => readFileSync(new URL(name, directory), 'utf8'));
  sources.push(read('installer/Yahad.Install.Core/ReleaseManifest.cs'));
  const found = new Set(sources.flatMap(source => [...literals(source)]));
  const prefixes = new Set(updateCodePrefixes.map(([prefix]) => prefix.slice(0, -1)));
  for (const code of found) {
    if (code in notStatusCodes) continue;
    // A literal that is a family prefix ("GITHUB_" + ...) is the family entry itself.
    if (prefixes.has(code.replace(/_$/, ''))) continue;
    assert.ok(resolveUpdateCode(code), `${code} has no catalog entry; add one or list why it never reaches the status`);
  }
  for (const code of Object.keys(notStatusCodes)) assert.ok(found.has(code), `${code} is listed as not a status code but no longer exists`);
  // Every exact entry belongs to a word the status accepts and matches the shape src/updates.ts keeps.
  for (const [code, entry] of Object.entries(updateCodes)) {
    assert.match(code, updateCodePattern, code); assert.ok(entry.word in updateErrorTexts, code);
  }
  // Unexpected failures are named by their exception type; the family still decides the text.
  assert.equal(resolveUpdateCode('GITHUB_HTTP:500')?.word, 'network');
  assert.equal(resolveUpdateCode('RELEASE_SOMETHING_NEW')?.word, 'signature');
  assert.equal(resolveUpdateCode('APP_PACKAGE_RUNTIME')?.word, 'archive');
  assert.equal(resolveUpdateCode('HttpRequestException:SecureConnectionError')?.word, 'network');
  assert.equal(resolveUpdateCode('IOException'), undefined);
});

test('every reason the Windows status path can give has a plain explanation', () => {
  const client = read('installer/Yahad.Platform/UpdaterClient.cs');
  const stages = [...client.matchAll(/(?:stage=|Refusal\(|Reason\(|UpdaterRefusal\(|Scm\()"([A-Z][A-Z0-9_]+)"/g)].map(match => match[1]).filter(stage => stage !== 'UPDATER_');
  // The Updater's refusal of a status read arrives as UPDATER_<its reply code>.
  const engine = read('installer/Yahad.Updater/UpdaterEngine.cs');
  const replies = [...engine.matchAll(/Refuse\("([A-Z_]+)"\)/g)].map(match => 'UPDATER_' + match[1]).filter(code => !['UPDATER_BUSY', 'UPDATER_STALE_TARGET'].includes(code));
  const transport = read('src/windows-update-transport.ts') + read('src/updates.ts');
  const local = [...transport.matchAll(/'(PLATFORM_[A-Z_]+|STATUS_SCHEMA|OPERATOR)'/g)].map(match => match[1]);
  for (const stage of new Set([...stages, ...replies, 'UPDATER_NO_STATUS', ...local]))
    assert.ok(stage in unavailableStages, `${stage} has no plain explanation`);
  assert.match(unavailableUpdatesText('SCM_SERVICE:5'), /אינו פועל/);
  assert.match(unavailableUpdatesText('UPDATER_NOT_AVAILABLE'), /מפתח החתימה/);
  assert.match(unavailableUpdatesText(undefined), /אינו זמין/);
});

test('a precise code refines its word, never another word, and the code is always kept for support', () => {
  const disk = describeUpdateFailure({ error: 'storage', code: 'DISK_SPACE', action: 'check' });
  assert.match(disk.todo, /1 GB/); assert.equal(disk.support, 'DISK_SPACE'); assert.equal(disk.retry, true);
  assert.match(describeUpdateFailure({ error: 'storage', code: 'DISK_SPACE', action: 'install' }).todo, /512 MB/);
  assert.match(describeUpdateFailure({ error: 'network', code: 'GITHUB_HTTP:403' }).title, /הגביל/);
  const unknown = describeUpdateFailure({ error: 'internal', code: 'IOException' });
  assert.equal(unknown.title, updateErrorTexts.internal.title); assert.equal(unknown.support, 'IOException');
  const foreign = describeUpdateFailure({ error: 'network', code: 'DISK_SPACE' });
  assert.equal(foreign.title, updateErrorTexts.network.title, 'a code of another word keeps the word\'s text');
  assert.equal(describeUpdateFailure({ error: 'internal', code: 'IOException', permanent: true }).retry, false, 'a version refused for good is never retried');
  assert.equal(describeUpdateFailure({ error: 'signature' }).support, 'signature', 'no code: the word itself');
  assert.equal(describeUpdateFailure({ phase: 'rolled_back' }).title, updateErrorTexts.health.title);
});

const parent = { role: 'parent', familyId: 'f', id: 'u' } as User;
const baseStatus = { enabled: true, mode: 'notify', modeControlAllowed: true, operator: { familyId: 'f', userId: 'u' }, phase: 'failed',
  currentVersion: '0.6.6', latestVersion: '0.6.7', checkedAt: 1000, updatedAt: Date.now(), error: 'signature', lastModeRequest: null,
  lastRequest: { id: '22222222-2222-4222-8222-222222222222', action: 'check', state: 'failed', startedAt: 900, finishedAt: 1000, error: 'signature', target: null } };
const native = (status: unknown): WindowsUpdateTransport => ({ status: async () => status, submit: async () => ({ state: 'queued' }) });
const parse = async (status: unknown) => await updateStatus({} as Config, parent, native(status)) as Record<string, any>;

test('the status carries the precise code and permanence, and reads an older Updater without them', async () => {
  const current = await parse({ ...baseStatus, errorCode: 'RELEASE_SIGNATURE_INVALID', errorPermanent: true,
    lastRequest: { ...baseStatus.lastRequest, code: 'RELEASE_SIGNATURE_INVALID' } });
  assert.equal(current.configured, true);
  assert.equal(current.errorCode, 'RELEASE_SIGNATURE_INVALID'); assert.equal(current.errorPermanent, true);
  assert.equal(current.lastRequest.code, 'RELEASE_SIGNATURE_INVALID');
  const older = await parse(baseStatus);
  assert.equal(older.configured, true);
  assert.equal(older.errorCode, undefined); assert.equal(older.errorPermanent, undefined); assert.equal(older.lastRequest.code, undefined);
  // A malformed precise field never hides the whole status.
  const malformed = await parse({ ...baseStatus, errorCode: 'C:\\Windows\\path', errorPermanent: 'yes', lastRequest: { ...baseStatus.lastRequest, code: 'x y' } });
  assert.equal(malformed.configured, true);
  assert.equal(malformed.errorCode, undefined); assert.equal(malformed.errorPermanent, undefined); assert.equal(malformed.lastRequest.code, undefined);
  assert.equal((await parse({ ...baseStatus, error: 'not_a_word' })).configured, false, 'the coarse word stays strict');
});

test('the update screen shows what happened, what to do and the precise support code', async () => {
  const status = await parse({ ...baseStatus, errorCode: 'RELEASE_SIGNATURE_INVALID', errorPermanent: true,
    lastRequest: { ...baseStatus.lastRequest, code: 'RELEASE_SIGNATURE_INVALID' } }) as ConfiguredUpdate;
  const progress = updateProgress(status);
  assert.equal(progress.title, updateErrorTexts.signature.title); assert.equal(progress.retry, false);
  const html = renderToStaticMarkup(React.createElement(UpdateProgressStatus, { data: status }));
  assert.ok(html.includes('קוד לתמיכה: <code dir="ltr"'));
  assert.ok(html.includes('>RELEASE_SIGNATURE_INVALID</code>'));
  assert.ok(html.includes(updateErrorTexts.signature.todo));
  // A scheduled failure without a journal is still explained, by its own code.
  const scheduled = updateProgress({ ...status, phase: 'idle', lastRequest: null, error: 'network', errorCode: 'GITHUB_HTTP:403', errorPermanent: false });
  assert.equal(scheduled.kind, 'failure'); assert.equal(scheduled.support, 'GITHUB_HTTP:403');
  // The journal's permanence is the Updater's only when it is the same failure.
  const otherFailure = updateProgress({ ...status, lastRequest: { ...status.lastRequest!, error: 'internal', code: 'IOException' } });
  assert.equal(otherFailure.retry, true);
  // A repair barrier names its code and gives steps, never a retry.
  const repair = updateProgress({ ...status, phase: 'recovery_required' });
  assert.equal(repair.retry, false); assert.ok(repair.todo && repair.support);
});

test('a refused update request keeps the server code and explains each one', async () => {
  setCsrf('synthetic');
  const original = globalThis.fetch;
  try {
    for (const code of [...Object.keys(updateRejectionTexts), 'SOMETHING_NEW']) {
      globalThis.fetch = (async () => new Response(JSON.stringify({ error: code }), { status: 409 })) as typeof fetch;
      await assert.rejects(submitUpdateRequest('check'), error => error instanceof RejectedUpdateRequest && error.code === code &&
        error.message === (updateRejectionTexts[code] ?? updateRejectionFallback));
    }
    globalThis.fetch = (async () => new Response('not json', { status: 403 })) as typeof fetch;
    await assert.rejects(submitUpdateRequest('check'), error => error instanceof RejectedUpdateRequest && error.code === 'HTTP_403');
  } finally { globalThis.fetch = original; }
  assert.notEqual(updateRejectionTexts.UPDATES_UNAVAILABLE, updateRejectionTexts.UPDATES_MODE_LOCKED);
});
