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
  const code = ts.transpileModule(`${source.slice(start, end)} return {text, setText, file, setFile, voice, setVoice, id, send, record}; }; Composer;`,
    { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
  let cursor = 0, nextId = 0; const slots: any[] = [], effects: (() => void)[] = [];
  const requests: { body: any; resolve: () => void; reject: (e: Error) => void }[] = [];
  const Composer = vm.runInNewContext(code, {
    useState(value: any) { const i = cursor++; if (!(i in slots)) slots[i] = value;
      return [slots[i], (v: any) => { slots[i] = typeof v === 'function' ? v(slots[i]) : v; }]; },
    useRef(value: any) { const i = cursor++; return slots[i] ||= { current: value }; },
    useEffect(fn: () => void, deps: any[]) { const i = cursor++, previous = slots[i]; slots[i] = deps;
      if (!previous || deps.some((d, j) => !Object.is(d, previous[j]))) effects.push(fn); },
    newId: () => `synthetic-${++nextId}`, useChatCelebration: () => ({ celebrate() {} }),
    api: (_path: string, _method: string, body: any) => new Promise<void>((resolve, reject) => requests.push({ body, resolve, reject })),
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
  const retry = state.send(); assert.equal(h.requests[1].body.clientId, h.requests[0].body.clientId);
  h.requests[1].resolve(); await retry; assert.equal(h.flush().text, '');
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
