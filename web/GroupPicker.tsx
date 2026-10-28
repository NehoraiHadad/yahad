import { compareChatActivity } from './chatOrder';
import React, { useEffect, useId, useRef, useState } from 'react';
import { GroupAvatar } from './GroupAvatar';

export type PickerGroup = { id: string; title: string; lastMessageTime?: number; kind?: 'group' | 'direct' };
export type PickerGrant = { user_id: string; group_id: string };
export function filterGroups(groups: PickerGroup[], query: string) {
  const search = query.normalize('NFKC').trim().toLocaleLowerCase();
  return search ? groups.filter(g => g.title.normalize('NFKC').toLocaleLowerCase().includes(search)) : groups;
}

export function GroupPicker({ groups, grants, childId, childName, showFilters, busy, loading, onToggle, photoRetryKey }: {
  photoRetryKey?: string | number;
  childName?: string; showFilters?: boolean;
  groups: PickerGroup[]; grants: PickerGrant[]; childId: string; busy: boolean; loading: boolean;
  onToggle: (groupId: string, allowed: boolean) => void;
}) {
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState<'all' | 'approved' | 'available'>('all');
  const lastToggle = useRef<HTMLInputElement | null>(null), summary = useRef<HTMLParagraphElement>(null);
  useEffect(() => {
    if (busy || !lastToggle.current) return;
    const control = lastToggle.current; lastToggle.current = null;
    // A saved approval can remove its row from the active filter. Never leave
    // keyboard users at the document root, and never steal an intentional focus move.
    if (document.activeElement !== document.body) return;
    if (control.isConnected && !control.disabled) control.focus();
    else summary.current?.focus();
  }, [busy, grants]);
  const labelId = useId(), helpId = useId();
  const selected = new Set(grants.filter(g => g.user_id === childId).map(g => g.group_id));
  const approvedCount = groups.filter(group => selected.has(group.id)).length;
  const visible = filterGroups(groups, query).filter(group => !showFilters || filter === 'all' || selected.has(group.id) === (filter === 'approved'))
    .slice().sort(compareChatActivity);
  return <div className="parent-group-picker">
    {showFilters && <div className="child-group-filters" role="group" aria-label="סינון שיחות">
      <button type="button" aria-pressed={filter === 'all'} onClick={() => setFilter('all')}>הכול ({groups.length})</button>
      <button type="button" aria-pressed={filter === 'approved'} onClick={() => setFilter('approved')}>מאושרות ({approvedCount})</button>
      <button type="button" aria-pressed={filter === 'available'} onClick={() => setFilter('available')}>להוספה ({groups.length - approvedCount})</button>
    </div>}
    <label htmlFor={labelId}>חיפוש שיחה לפי שם</label>
    <div className="group-search">
      <input id={labelId} type="search" autoComplete="off" maxLength={200} value={query}
        aria-describedby={helpId} placeholder="הקלידו שם שיחה…" onChange={e => setQuery(e.target.value)} />
      {query && <button type="button" className="text-button" onClick={() => setQuery('')}>ניקוי חיפוש</button>}
    </div>
    <p ref={summary} tabIndex={-1} id={helpId} className="group-picker-summary" role="status" aria-live="polite">
      {loading ? 'טוענים שיחות…' : `${visible.length} מתוך ${groups.length} שיחות · ${approvedCount} מאושרות ${childName ? 'עבור ' + childName : 'לילד שנבחר'}`}
    </p>
    {!childId && <p className="group-picker-summary">הוסיפו חשבון ילד ובחרו אותו לפני אישור שיחות.</p>}
    <div className="approval-list scrollable-group-list" role="region" aria-label={'שיחות לאישור עבור ' + (childName || 'הילד שנבחר')} tabIndex={0} aria-busy={busy || loading}>
      {visible.map(g => {
        const checked = selected.has(g.id);
        return <label key={g.id} className={'approval-row ' + (checked ? 'approved' : '')}>
          <GroupAvatar groupId={g.id} title={g.title} retryKey={photoRetryKey} />
          <span className="group-choice-title"><b>{g.title}</b>{g.kind && <small>{g.kind === 'direct' ? 'שיחה פרטית' : 'קבוצה'}</small>}<small>{checked ? 'מאושרת · זמינה בחשבון' : 'לא מאושרת · סמנו להוספה'}</small></span>
          <input type="checkbox" aria-label={'אישור שיחה ' + g.title + (childName ? ' עבור ' + childName : '')} checked={checked} disabled={busy || loading || !childId}
            onChange={e => { lastToggle.current = e.currentTarget; onToggle(g.id, e.target.checked); }} />
          <span className="custom-check" aria-hidden="true">{checked ? '✓' : ''}</span>
        </label>;
      })}
      {!loading && !groups.length && <div className="picker-empty"><p>אין שיחות ברשימה. לאחר החיבור אפשר לרענן את רשימת השיחות.</p></div>}
      {!loading && !!groups.length && !visible.length && <div className="picker-empty"><p>{query ? 'לא נמצאו שיחות בשם הזה במסנן הנוכחי. נסו שם אחר או נקו את החיפוש.' : filter === 'approved' ? 'עדיין אין שיחות מאושרות לחשבון הזה.' : 'כל השיחות כבר מאושרות לחשבון הזה.'}</p>
        {showFilters && filter !== 'all' && <button type="button" className="text-button" onClick={() => { setFilter('all'); setQuery(''); }}>הצגת כל השיחות</button>}</div>}
    </div>
  </div>;
}
