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

export function searchChatGroups<T extends { title: string }>(groups: T[], query: string): T[] {
  const normalized = query.normalize('NFKC').trim().toLocaleLowerCase();
  return normalized ? groups.filter(group => group.title.normalize('NFKC').toLocaleLowerCase().includes(normalized)) : groups;
}

export function parentEmptyMessage(filter: ChatFilter, overview: FamilyOverview | null, verified: boolean): string {
  if (filter.mode === 'all') return 'עדיין אין קבוצות ברשימה. בדקו את החיבור ורעננו את הקבוצות בניהול ההורה.';
  if (!verified) return 'מאמתים הרשאות לפני הצגת שיחות.';
  if (!overview?.children.length) return 'עדיין אין חשבונות ילדים. אפשר להוסיף ילד או ילדה בניהול ההורה.';
  if (!overview.children.some(child => child.id === filter.childId)) return 'בחרו ילד או ילדה כדי להציג קבוצות מאושרות.';
  return 'אין קבוצות מאושרות זמינות לחשבון שנבחר.';
}

export function ParentChatFilter({ filter, overview, verified, onChange }: {
  filter: ChatFilter; overview: FamilyOverview | null; verified: boolean; onChange: (filter: ChatFilter) => void;
}) {
  return <section className="parent-chat-filter" aria-label="סינון שיחות להורה">
    <div className="parent-chat-modes" role="group" aria-label="תצוגת שיחות">
      <button type="button" aria-pressed={filter.mode === 'all'} onClick={() => onChange({ ...filter, mode: 'all' })}>כל השיחות</button>
      <button type="button" aria-pressed={filter.mode === 'child'} onClick={() => onChange({ ...filter, mode: 'child' })}>לפי ילד</button>
    </div>
    {filter.mode === 'all' && <p>כל הקבוצות שסונכרנו בחשבון ההורה, לא שיחות פרטיות. תוכן מוצג רק בקבוצות שאושרו לילד אחד לפחות.</p>}
    {filter.mode === 'child' && <>
      <label>בחירת ילד או ילדה
        <select disabled={!verified || !overview?.children.length}
          value={overview?.children.some(child => child.id === filter.childId) ? filter.childId : ''}
          onChange={event => onChange({ mode: 'child', childId: event.target.value })}>
          <option value="">בחרו חשבון ילד</option>
          {overview?.children.map(child => <option key={child.id} value={child.id}>{childLabel(child)}</option>)}
        </select>
      </label>
      <p>סינון קבוצות בלבד. נשארים בחשבון ההורה; הרשאות השליחה וההיסטוריה עשויות להיות שונות מחשבון הילד.</p>
      {!verified && <p role="status">ממתינים לאימות הקבוצות המאושרות מול השרת.</p>}
    </>}
  </section>;
}
