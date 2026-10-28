import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readRoute, writeRoute } from '../web/browserNavigation.js';

test('management → chats → group supports Back/Forward without leaving the app or duplicate entries', () => {
  const entries: unknown[] = [null]; let index = 0;
  const history = {
    get state() { return entries[index]; },
    pushState(value: unknown) { entries.splice(++index); entries[index] = value; },
    replaceState(value: unknown) { entries[index] = value; },
  };
  writeRoute(history, { mode: 'parent', group: null }, 'parent', true);
  writeRoute(history, { mode: 'chat', group: null }, 'parent');
  writeRoute(history, { mode: 'chat', group: 'synthetic-group' }, 'parent');
  writeRoute(history, { mode: 'chat', group: 'synthetic-group' }, 'parent');
  assert.equal(entries.length, 3);
  index--; assert.deepEqual(readRoute(history.state, 'parent', 'parent'), { mode: 'chat', group: null });
  index--; assert.deepEqual(readRoute(history.state, 'parent', 'parent'), { mode: 'parent', group: null });
  index += 2; assert.deepEqual(readRoute(history.state, 'parent', 'parent'), { mode: 'chat', group: 'synthetic-group' });
});

test('history is account-bound, never grants access, and ignores invalid routes', () => {
  assert.equal(readRoute({ yachadRoute: { userId: 'old-user', mode: 'chat', group: 'private' } }, 'child', 'child'), null);
  assert.equal(readRoute({ yachadRoute: { userId: 'child', mode: 'parent' } }, 'child', 'child'), null);
  assert.equal(readRoute({ yachadRoute: { userId: 'child', mode: 'unknown' } }, 'child', 'child'), null);
  assert.equal(readRoute(null, 'parent', 'parent'), null);
});

test('actual App navigation restores history on popstate and clears stale errors', async () => {
  const { readFileSync } = await import('node:fs');
  const { default: vm } = await import('node:vm');
  const { default: ts } = await import('typescript');
  const source = readFileSync(new URL('../web/main.tsx', import.meta.url), 'utf8');
  const start = source.indexOf('function App()'), end = source.indexOf('  if (!loaded)', start);
  const code = ts.transpileModule(`${source.slice(start, end)}return {route, navigate, refresh, error, setError}; }; App;`,
    { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
  const entries: any[] = [null]; let position = 0, cursor = 0;
  const slots: any[] = [], handlers = new Map<string, () => void>();
  const history = { get state() { return entries[position]; },
    pushState(s: unknown) { entries.splice(++position); entries[position] = s; },
    replaceState(s: unknown) { entries[position] = s; } };
  const App = vm.runInNewContext(code, {
    readRoute, writeRoute, history,
    useState(value: any) { const i = cursor++; if (!(i in slots)) slots[i] = value;
      return [slots[i], (v: any) => slots[i] = typeof v === 'function' ? v(slots[i]) : v]; },
    useCallback(fn: any) { return fn; },
    useEffect(fn: () => void) { if (fn.toString().includes('popstate')) fn(); },
    api: async () => ({ user: { id: 'synthetic-parent', role: 'parent' }, csrf: 'synthetic' }),
    setCsrf() {}, localStorage: { getItem() { return null; } },
    window: { addEventListener(type: string, fn: () => void) { handlers.set(type, fn); }, removeEventListener() {} },
  });
  const render = () => { cursor = 0; return App(); };
  await render().refresh();
  let app = render();
  assert.equal(app.route.mode, 'parent');
  app.navigate({ mode: 'chat', group: null });
  app = render(); app.navigate({ mode: 'chat', group: 'synthetic' });
  app = render(); app.setError('old failure');
  position--; handlers.get('popstate')!(); app = render();
  assert.equal(app.route.mode, 'chat'); assert.equal(app.route.group, null); assert.equal(app.error, '');
  position--; handlers.get('popstate')!(); assert.equal(render().route.mode, 'parent');
  position += 2; handlers.get('popstate')!(); assert.equal(render().route.group, 'synthetic');
  await render().refresh(); assert.equal(render().route.group, 'synthetic', 'reload retains route');
});
