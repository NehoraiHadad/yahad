import type { FamilyOverview } from './FamilyProgress';

export type ChildGrantChange = { childId: string; groupId: string; allowed: boolean };

/** Selection is an account ID, never a name or an array position. */
export function reconcileChildSelection(data: FamilyOverview, selected: string) {
  if (data.children.some(child => child.id === selected)) return selected;
  const knownGroups = new Set(data.groups.map(group => group.id));
  const granted = new Set(data.grants.filter(grant => knownGroups.has(grant.group_id)).map(grant => grant.user_id));
  return data.children.find(child => !granted.has(child.id))?.id || data.children[0]?.id || '';
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
