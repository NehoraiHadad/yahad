import { familyProgress, type FamilyOverview } from './FamilyProgress';

export type ChildGrantChange = { childId: string; groupId: string; allowed: boolean };
export type ChildAccessChange = { childId: string; paused: boolean; revision: number };

/** Selection is an account ID, never a name or an array position. */
export function reconcileChildSelection(data: FamilyOverview, selected: string) {
  if (data.children.some(child => child.id === selected)) return selected;
  const progress = familyProgress(data);
  return progress.pendingChild?.id || progress.children[0]?.id || '';
}

/** Only a current server snapshot can confirm an individual child's change. */
export function confirmedChildGrant(data: FamilyOverview, change: ChildGrantChange, stale: boolean) {
  if (stale || !data.children.some(child => child.id === change.childId) ||
      !data.groups.some(group => group.id === change.groupId)) return false;
  return data.grants.some(grant => grant.user_id === change.childId && grant.group_id === change.groupId) === change.allowed;
}

/** Reject duplicate clicks synchronously; invalidation prevents stale feedback on switch/close. */
export class ChildActionGuard {
  private generation = 0;
  private active = false;
  begin(): number | null { if (this.active) return null; this.active = true; return ++this.generation; }
  finish(request: number): boolean { if (request !== this.generation) return false; this.active = false; return true; }
  invalidate() { this.generation++; this.active = false; }
}

/** Remember only this parent's UI selection in this tab; grants always come from the server. */
export function readChildSelection(userId: string, storage?: Pick<Storage, 'getItem'>): string {
  try { return (storage || sessionStorage).getItem(`yachad-selected-child:${userId}`) || ''; }
  catch { return ''; }
}
export function saveChildSelection(userId: string, childId: string, storage?: Pick<Storage, 'setItem'>) {
  try { (storage || sessionStorage).setItem(`yachad-selected-child:${userId}`, childId); }
  catch { /* Storage-disabled browsers still support the current in-memory selection. */ }
}
