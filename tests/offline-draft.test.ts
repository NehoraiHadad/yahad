import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';

// Actual Composer hooks, with no browser, provider, disk persistence or real data.
function harness(extra: Record<string, unknown> = {}) {
  const source = readFileSync(new URL('../web/main.tsx', import.meta.url), 'utf8');
  const start = source.indexOf('function Composer('), end = source.indexOf('  return (\n    <div className="composer-wrap"', start);
  assert.ok(end > start);
  const code = ts.transpileModule(`${source.slice(start, end)} return {text, setText, file, setFile, voice, setVoice, id, send, record, sendUncertain, checkSend}; }; Composer;`,
    { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
  let cursor = 0, nextId = 0; const slots: any[] = [], effects: (() => void)[] = [];
  const requests: { path: string; method: string; body: any; resolve: (result?: any) => void; reject: (e: Error) => void }[] = [];
  const Composer = vm.runInNewContext(code, {
    useState(value: any) { const i = cursor++; if (!(i in slots)) slots[i] = value;
      return [slots[i], (v: any) => { slots[i] = typeof v === 'function' ? v(slots[i]) : v; }]; },
    useRef(value: any) { const i = cursor++; return slots[i] ||= { current: value }; },
    useEffect(fn: () => void, deps: any[]) { const i = cursor++, previous = slots[i]; slots[i] = deps;
      if (!previous || deps.some((d, j) => !Object.is(d, previous[j]))) effects.push(fn); },
    newId: () => `synthetic-${++nextId}`, useChatCelebration: () => ({ celebrate() {} }),
    api: (path: string, method = 'GET', body: any) => new Promise<any>((resolve, reject) => requests.push({ path, method, body, resolve, reject })),
    URL: { createObjectURL() { return 'blob:synthetic'; }, revokeObjectURL() {} },
    ...extra,
  });
  let props: any = { group: 'synthetic-group', messages: [], caps: {}, enabled: true, suspended: false, editing: null, reply: null, onBusyChange() {}, onError() {}, onSent() {} };
  const render = () => { cursor = 0; return Composer(props); };
  const flush = (patch = {}) => { props = { ...props, ...patch }; let result = render();
    while (effects.length) { effects.splice(0).forEach(fn => fn()); result = render(); } return result; };
  return { flush, requests };
}

test('temporary disconnect retains typed draft and idempotency key, blocks send, and resumes after verification', async () => {
  const h = harness(); let state = h.flush();
  state.setText('טיוטה סינתטית'); state = h.flush(); const id = state.id.current;
  state = h.flush({ suspended: true, enabled: false });
  assert.equal(state.text, 'טיוטה סינתטית'); assert.equal(state.id.current, id);
  await state.send(); assert.equal(h.requests.length, 0);
  state = h.flush({ suspended: false, enabled: true });
  const sending = state.send(); h.requests[0].reject(new Error('synthetic connection lost')); await sending;
  state = h.flush({ suspended: true, enabled: false });
  assert.equal(state.text, 'טיוטה סינתטית'); assert.equal(state.id.current, id);
  state = h.flush({ suspended: false, enabled: true });
  await state.send(); assert.equal(h.requests.length, 1, 'unknown result never triggers another POST');
  assert.equal(state.sendUncertain, true);
  const check = state.checkSend(); assert.equal(h.requests[1].method, 'GET');
  assert.ok(h.requests[1].path.endsWith('/outgoing/' + id));
  h.requests[1].resolve({ state: 'confirmed' }); await check; assert.equal(h.flush().text, '');
});

test('local attachments survive suspension; copied message edit content does not', () => {
  const h = harness(); let state = h.flush();
  const file = { name: 'synthetic.txt' }, voice = { synthetic: true };
  state.setFile(file); state.setVoice(voice); state = h.flush({ suspended: true, enabled: false });
  assert.equal(state.file, file); assert.equal(state.voice, voice);
  state = h.flush({ suspended: false, enabled: true, editing: { id: 'synthetic-message', text: 'server message text' } });
  assert.equal(state.text, 'server message text');
  state = h.flush({ suspended: true, enabled: false, editing: null }); assert.equal(state.text, '', 'batched suspension and edit dismissal must clear copied text');
  assert.equal(harness().flush().text, '', 'fresh Chat/account cannot recover previous draft');
});

test('Composer is a stable sibling outside the permission-gated message subtree and is hidden while suspended', () => {
  const source = readFileSync(new URL('../web/main.tsx', import.meta.url), 'utf8');
  const composer = source.indexOf('            <Composer');
  assert.ok(composer > source.indexOf('          <ConversationVerification'));
  assert.match(source, /\{composerGroup && \(/);
  assert.match(source, /<Chat\s+key=\{me.user.id\}/);
  assert.match(source, /enabled=\{!!activeSelected && online && me.connection === 'connected'\}/);
  assert.match(source, /className="composer-wrap" hidden=\{suspended\}/);
});


test('microphone permission resolving after suspension stops tracks and cannot start hidden recording', async () => {
  let resolve!: (stream: any) => void, stopped = 0, recorders = 0;
  const h = harness({ window: { isSecureContext: true, MediaRecorder: true },
    navigator: { mediaDevices: { getUserMedia: () => new Promise(r => { resolve = r; }) } },
    MediaRecorder: class { constructor() { recorders++; } },
  });
  const pending = h.flush().record();
  h.flush({ suspended: true, enabled: false });
  resolve({ getTracks: () => [{ stop() { stopped++; } }] }); await pending;
  assert.equal(stopped, 1); assert.equal(recorders, 0);
  await h.flush().record(); assert.equal(recorders, 0);
});

test('pending, unknown and missing outcomes preserve the draft; proven non-submission unlocks explicit retry only', async () => {
  const h = harness(); let state = h.flush(); state.setText('synthetic uncertain draft'); state = h.flush();
  const id = state.id.current, send = state.send(); h.requests[0].reject(new Error('lost response')); await send;
  for (const result of ['pending', 'unknown', 'not_found']) {
    state = h.flush(); const check = state.checkSend(); h.requests.at(-1)!.resolve({ state: result }); await check;
    state = h.flush(); assert.equal(state.sendUncertain, true); assert.equal(state.text, 'synthetic uncertain draft');
    assert.equal(state.id.current, id); await state.send();
  }
  const check = state.checkSend(); h.requests.at(-1)!.resolve({ state: 'failed' }); await check;
  state = h.flush(); assert.equal(state.sendUncertain, false); assert.equal(h.requests.filter(r => r.method === 'POST').length, 1);
  const retry = state.send(); assert.equal(h.requests.at(-1)!.body.clientId, id);
  h.requests.at(-1)!.resolve({ outcome: 'confirmed' }); await retry; assert.equal(h.flush().text, '');
});

test('known preparation failure keeps the same draft ready, with no automatic retry', async () => {
  const h = harness(); let state = h.flush(); state.setText('synthetic'); state = h.flush();
  const send = state.send(); h.requests[0].reject(Object.assign(new Error('not submitted'), { code: 'SEND_NOT_SUBMITTED', sendOutcome: 'not_submitted' })); await send;
  assert.equal(h.flush().sendUncertain, false); assert.equal(h.requests.length, 1);
});

test('changing a safe failed payload rotates its ID for removed attachments, replacement voice and changed quotes', async () => {
  class SyntheticForm { fields = new Map(); set(name: string, value: any) { this.fields.set(name, value); } }
  for (const change of ['file', 'voice', 'reply']) {
    const h = harness({ FormData: SyntheticForm }); let state = h.flush({ reply: change === 'reply' ? { id: 'original-quote' } : null });
    state.setText('Synthetic editable draft');
    if (change === 'file') state.setFile({ name: 'synthetic.txt' });
    if (change === 'voice') state.setVoice({ synthetic: 'original recording' });
    state = h.flush(); const old = state.id.current, first = state.send();
    h.requests[0].reject(Object.assign(new Error('proven preflight rejection'), { sendOutcome: 'not_submitted' })); await first;
    state = h.flush();
    if (change === 'file') state.setFile(null);
    if (change === 'voice') state.setVoice({ synthetic: 'replacement recording' });
    state = h.flush(change === 'reply' ? { reply: null } : {});
    const second = state.send();
    assert.notEqual(state.id.current, old, change + ' mutation needs a fresh logical send ID');
    h.requests[1].resolve(); await second;
  }
});

test('microphone permission resolving during a send cannot start recording behind pending or uncertain controls', async () => {
  let resolve!: (stream: any) => void, stopped = 0, recorders = 0;
  const h = harness({ window: { isSecureContext: true, MediaRecorder: true },
    navigator: { mediaDevices: { getUserMedia: () => new Promise(r => { resolve = r; }) } },
    MediaRecorder: class { constructor() { recorders++; } },
  });
  let state = h.flush(); const permission = state.record(); state.setText('Synthetic send'); state = h.flush();
  const sending = state.send();
  resolve({ getTracks: () => [{ stop() { stopped++; } }] }); await permission;
  assert.equal(stopped, 1); assert.equal(recorders, 0);
  h.requests[0].reject(new Error('Synthetic lost response')); await sending;
  await h.flush().record(); assert.equal(recorders, 0);
});
