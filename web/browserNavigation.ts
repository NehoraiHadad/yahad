export type AppRoute = { mode: 'parent' | 'chat'; group: string | null };
const key = 'yachadRoute';
// Account-bound history prevents Back/Forward from restoring another account's view.
export function readRoute(state: unknown, userId: string, role: string): AppRoute | null {
  const entry = (state as Record<string, any> | null)?.[key];
  if (!entry || entry.userId !== userId || !['parent', 'chat'].includes(entry.mode)) return null;
  if (entry.mode === 'parent' && role !== 'parent') return null;
  return { mode: entry.mode, group: entry.mode === 'chat' && typeof entry.group === 'string' ? entry.group : null };
}
export function writeRoute(history: Pick<History, 'state' | 'pushState' | 'replaceState'>, route: AppRoute, userId: string, replace = false) {
  const current = readRoute(history.state, userId, 'parent');
  if (!replace && current?.mode === route.mode && current.group === route.group) return;
  history[replace ? 'replaceState' : 'pushState']({ ...history.state, [key]: { ...route, userId } }, '');
}
