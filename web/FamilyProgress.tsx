import { compareChatActivity } from './chatOrder';
import React from 'react';
import type { PickerGrant, PickerGroup } from './GroupPicker';
import { childLabel } from './childLabels';

export type FamilyChild = { id: string; name: string; senderLabel?: string };
export type FamilyOverview = {
  children: FamilyChild[]; groups: PickerGroup[]; grants: PickerGrant[];
  state: string; linked?: boolean; disconnect?: string | null; disconnectHistory?: string | null;
};
/** Derived only from server-persisted accounts/grants and current connector state.
 * This is setup readiness, never evidence that a child device has signed in. */
export function familyProgress(data: FamilyOverview) {
  const connected = data.state === 'connected' && !data.disconnect;
  const children = data.children.slice().sort((a, b) => a.name.localeCompare(b.name, 'he') || a.id.localeCompare(b.id)).map(child => {
    const granted = new Set(data.grants.filter(grant => grant.user_id === child.id).map(grant => grant.group_id));
    const groups = data.groups.filter(group => granted.has(group.id))
      .slice().sort(compareChatActivity);
    return { ...child, groups, ready: connected && groups.length > 0 };
  });
  const pendingChild = children.find(child => !child.groups.length);
  const next = !connected ? 'connect' : !children.length ? 'child' : pendingChild ? 'groups' : 'login';
  return { connected, children, pendingChild, next,
    completed: Number(connected) + Number(children.length > 0) + Number(children.length > 0 && !pendingChild) };
}

export function FamilyProgress({ data, busy, stale, onAdd, onSelect }: {
  data: FamilyOverview; busy: boolean; stale: boolean; onAdd: () => void; onSelect: (id: string) => void;
}) {
  const progress = familyProgress(data);
  const nextText = { connect: 'הצעד הבא: חיבור החשבון', child: 'הצעד הבא: הוספת ילד או ילדה',
    groups: `הצעד הבא: בחירת שיחות עבור ${progress.pendingChild ? childLabel(progress.pendingChild) : ''}`,
    login: 'הצעד הבא: כניסה בטאבלט עם חשבון הילד' }[progress.next];
  return <section className="parent-card family-progress" aria-labelledby="family-progress-title">
    <h2 id="family-progress-title">ממשיכים מהמקום שבו עצרתם</h2>
    <p>החשבונות והשיחות נשמרים בשרת. אחרי רענון, ההתקדמות מחושבת מהם מחדש.</p>
    <ol className="setup-checklist" aria-label="התקדמות ההכנה">
      <li>{progress.connected ? '✓' : '○'} חיבור החשבון</li>
      <li>{progress.children.length ? '✓' : '○'} חשבון ילד</li>
      <li>{progress.children.length > 0 && !progress.pendingChild ? '✓' : '○'} שיחות לכל ילד</li>
    </ol>
    <p role="status">{progress.completed} מתוך 3 שלבי הכנה{stale ? ' · המידע ממתין לרענון' : ''}</p>
    {progress.next === 'child'
      ? <button type="button" className="outline-button" disabled={busy || stale} onClick={onAdd}>{nextText}</button>
      : <a className="setup-next-link" href={progress.next === 'connect' ? '#family-connection' : progress.next === 'groups' ? '#family-groups' : '#family-tablet'}
          aria-disabled={busy || stale} onClick={event => { if (busy || stale) { event.preventDefault(); return; } if (progress.next === 'groups' && progress.pendingChild) onSelect(progress.pendingChild.id); }}>{nextText}</a>}
    <p className="muted">מוכנות לכניסה אינה אישור שהטאבלט התחבר. אין כאן מעקב אחר חיבור מכשירי הילדים.</p>
  </section>;
}

export function ChildGroupSummary({ data, selected, busy, stale = false, onSelect }: {
  data: FamilyOverview; selected: string; busy: boolean; stale?: boolean; onSelect: (id: string) => void;
}) {
  const progress = familyProgress(data);
  return <section className="parent-card child-group-summary" aria-labelledby="child-summary-title">
    <h2 id="child-summary-title">הילדים והשיחות המאושרות</h2>
    <p>שמות השיחות בלבד — בלי להיכנס לחשבון הילד ובלי תצוגה מקדימה של הודעות.</p>
    {!progress.children.length && <p>עדיין אין חשבונות ילדים. חשבון חדש מתחיל בלי שיחות מאושרות.</p>}
    <ul className="child-summary-list">{progress.children.map(child => <li key={child.id}>
      <div className="child-summary-heading"><h3>{childLabel(child)}</h3>
        <button type="button" className="text-button" disabled={busy} aria-pressed={selected === child.id}
          onClick={() => { onSelect(child.id); document.getElementById('family-groups')?.focus(); }}>
          בחירת שיחות עבור {childLabel(child)}</button></div>
      <p>{child.groups.length} שיחות מאושרות · {stale ? 'ממתינים לאימות מול השרת' : child.ready ? 'מוכן לכניסה בטאבלט' : !child.groups.length ? 'נדרשת בחירת שיחות' : 'נדרש חיבור החשבון'}</p>
      {!!child.groups.length && <ul className="child-group-titles" aria-label={'שיחות מאושרות עבור ' + childLabel(child)}>
        {child.groups.map(group => <li key={group.id}>{group.title}</li>)}
      </ul>}
    </li>)}</ul>
  </section>;
}
