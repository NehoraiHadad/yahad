import React, { useId, useState } from 'react';
import { GroupAvatar } from './GroupAvatar';

export type PickerGroup = { id: string; title: string };
export type PickerGrant = { user_id: string; group_id: string };
export function filterGroups(groups: PickerGroup[], query: string) {
  const search = query.normalize('NFKC').trim().toLocaleLowerCase();
  return search ? groups.filter(g => g.title.normalize('NFKC').toLocaleLowerCase().includes(search)) : groups;
}

export function GroupPicker({ groups, grants, childId, busy, loading, onToggle, photoRetryKey }: {
  photoRetryKey?: string | number;
  groups: PickerGroup[]; grants: PickerGrant[]; childId: string; busy: boolean; loading: boolean;
  onToggle: (groupId: string, allowed: boolean) => void;
}) {
  const [query, setQuery] = useState('');
  const labelId = useId(), helpId = useId();
  const selected = new Set(grants.filter(g => g.user_id === childId).map(g => g.group_id));
  const visible = filterGroups(groups, query).slice().sort((a, b) => a.title.localeCompare(b.title, 'he') || a.id.localeCompare(b.id));
  return <div className="parent-group-picker">
    <label htmlFor={labelId}>חיפוש קבוצה לפי שם</label>
    <div className="group-search">
      <input id={labelId} type="search" autoComplete="off" maxLength={200} value={query}
        aria-describedby={helpId} placeholder="הקלידו שם קבוצה…" onChange={e => setQuery(e.target.value)} />
      {query && <button type="button" className="text-button" onClick={() => setQuery('')}>ניקוי חיפוש</button>}
    </div>
    <p id={helpId} className="group-picker-summary" role="status" aria-live="polite">
      {loading ? 'טוענים קבוצות…' : `${visible.length} מתוך ${groups.length} קבוצות · ${selected.size} מאושרות לילד שנבחר`}
    </p>
    {!childId && <p className="group-picker-summary">הוסיפו חשבון ילד ובחרו אותו לפני אישור קבוצות.</p>}
    <div className="approval-list scrollable-group-list" role="region" aria-label="קבוצות לאישור עבור הילד שנבחר" tabIndex={0} aria-busy={busy || loading}>
      {visible.map(g => {
        const checked = selected.has(g.id);
        return <label key={g.id} className={'approval-row ' + (checked ? 'approved' : '')}>
          <GroupAvatar groupId={g.id} title={g.title} retryKey={photoRetryKey} />
          <span className="group-choice-title"><b>{g.title}</b><small>{checked ? 'מאושרת · מוצגת לילד' : 'לא מאושרת'}</small></span>
          <input type="checkbox" aria-label={'אישור קבוצה ' + g.title} checked={checked} disabled={busy || loading || !childId}
            onChange={e => onToggle(g.id, e.target.checked)} />
          <span className="custom-check" aria-hidden="true">{checked ? '✓' : ''}</span>
        </label>;
      })}
      {!loading && !groups.length && <div className="picker-empty"><p>אין קבוצות ברשימה. לאחר החיבור אפשר לרענן את רשימת הקבוצות.</p></div>}
      {!loading && !!groups.length && !visible.length && <div className="picker-empty"><p>לא נמצאו קבוצות בשם הזה. נסו שם אחר או נקו את החיפוש.</p></div>}
    </div>
  </div>;
}
