import React from 'react';
import type { FamilyOverview } from './FamilyProgress';
import { childLabel } from './childLabels';

export type ChatFilter = { mode: 'all' | 'child'; childId: string };
/** This narrows the parent's own groups. It never changes identity or permissions. */
export function filterParentGroups<T extends { id: string }>(groups: T[], filter: ChatFilter, overview: FamilyOverview | null, verified: boolean): T[] {
  if (filter.mode === 'all') return groups;
  if (!verified || !overview?.children.some(child => child.id === filter.childId)) return [];
  const granted = new Set(overview.grants.filter(grant => grant.user_id === filter.childId).map(grant => grant.group_id));
  return groups.filter(group => granted.has(group.id));
}

export function ParentChatFilter({ filter, overview, verified, onChange }: {
  filter: ChatFilter; overview: FamilyOverview | null; verified: boolean; onChange: (filter: ChatFilter) => void;
}) {
  return <section className="parent-chat-filter" aria-label="סינון שיחות להורה">
    <div className="parent-chat-modes" role="group" aria-label="תצוגת שיחות">
      <button type="button" aria-pressed={filter.mode === 'all'} onClick={() => onChange({ ...filter, mode: 'all' })}>כל השיחות</button>
      <button type="button" aria-pressed={filter.mode === 'child'} onClick={() => onChange({ ...filter, mode: 'child' })}>לפי ילד</button>
    </div>
    {filter.mode === 'child' && <>
      <label>בחירת ילד או ילדה
        <select value={overview?.children.some(child => child.id === filter.childId) ? filter.childId : ''}
          onChange={event => onChange({ mode: 'child', childId: event.target.value })}>
          <option value="">בחרו חשבון ילד</option>
          {overview?.children.map(child => <option key={child.id} value={child.id}>{childLabel(child, overview.children)}</option>)}
        </select>
      </label>
      <p>סינון קבוצות בלבד. נשארים בחשבון ההורה; הרשאות השליחה וההיסטוריה עשויות להיות שונות מחשבון הילד.</p>
      {!verified && <p role="status">ממתינים לאימות הקבוצות המאושרות מול השרת.</p>}
    </>}
  </section>;
}
