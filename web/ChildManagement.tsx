import React, { useEffect, useRef, useState } from 'react';
import { Check, ChevronLeft, LoaderCircle, Plus, RefreshCw, Settings, ShieldCheck, Users } from 'lucide-react';
import { familyProgress, type FamilyOverview } from './FamilyProgress';
import { childLabel } from './childLabels';
import { GroupPicker } from './GroupPicker';
import { ChildActionGuard, confirmedChildGrant, type ChildGrantChange } from './childManagementState';

type Props = {
  data: FamilyOverview | null; selected: string; busy: boolean; stale: boolean; createdChild: string;
  photoRetryKey?: string; onSelect: (id: string) => void; onAdd: () => void;
  onSettings: (id: string, mode: 'profile' | 'password') => void;
  onGrant: (change: ChildGrantChange) => Promise<boolean>; onRefresh: () => void; onRetry: () => void;
  children?: React.ReactNode;
};

export function ChildManagement(props: Props) {
  const { data, selected, busy, stale, onSelect, onAdd } = props;
  const roster = useRef<HTMLUListElement>(null);
  useEffect(() => {
    const card = roster.current?.querySelector<HTMLElement>('[aria-pressed="true"]');
    if (!card || !roster.current) return;
    const list = roster.current, box = card.getBoundingClientRect(), viewport = list.getBoundingClientRect();
    // Scroll only the roster, not the entire page or the selected workspace.
    if (box.top < viewport.top) list.scrollTop += box.top - viewport.top;
    else if (box.bottom > viewport.bottom) list.scrollTop += box.bottom - viewport.bottom;
  }, [selected, data?.children.map(child => `${child.id}:${child.name}`).join('|')]);
  const children = data ? familyProgress(data).children : [];
  const child = children.find(child => child.id === selected);
  return <section className="child-management parent-card" aria-labelledby="children-title" aria-busy={!data && !stale}>
    <header className="children-heading">
      <div><span className="eyebrow">המרחב המשפחתי</span><h2 id="children-title" tabIndex={-1}>הילדים שלכם <span className="children-count">{data ? children.length : '—'}</span></h2>
        <p>בוחרים ילד או ילדה, ומנהלים את הקבוצות והחשבון שלהם במקום אחד.</p></div>
      <button type="button" className="primary" disabled={busy || stale || !data} onClick={onAdd}><Plus size={18}/> הוספת ילד או ילדה</button>
    </header>
    {!data ? <div className="child-workspace-empty" role={stale ? 'alert' : 'status'}>
      {stale ? <><h3>לא הצלחנו לטעון את הילדים</h3><p>לא בוצעו שינויים. בדקו את החיבור ונסו שוב.</p><button type="button" className="outline-button" onClick={props.onRetry}>ניסיון נוסף</button></>
        : <><LoaderCircle className="spin" size={24}/><p>טוענים את הילדים והקבוצות שלהם…</p></>}
    </div> : !children.length ? <div className="child-workspace-empty">
      <Users size={32}/><h3>כאן יופיעו הילדים שלכם</h3><p>הוסיפו חשבון ראשון. לאחר מכן בחרו עבורו קבוצות — אף קבוצה לא מאושרת אוטומטית.</p>
      <button type="button" className="outline-button" disabled={busy || stale} onClick={onAdd}><Plus size={18}/> הוספת החשבון הראשון</button>
    </div> : <div className="child-workspace">
      <nav className="child-roster" aria-label="בחירת ילד או ילדה">
        <p className="child-roster-label">בחרו חשבון לניהול</p>
        {children.length > 4 && <p className="child-roster-help">הרשימה נגללת להצגת כל הילדים</p>}
        <ul ref={roster} tabIndex={0} aria-label="רשימת הילדים">{children.map(account => <li key={account.id}>
          <button type="button" className="child-card" aria-pressed={selected === account.id} aria-controls="family-groups" disabled={busy}
            onClick={() => onSelect(account.id)} aria-label={`ניהול החשבון של ${childLabel(account)}`}>
            <span className="child-card-top"><span className="child-initial" aria-hidden="true">{Array.from(account.name)[0]}</span>
              <strong dir="auto">{childLabel(account)}</strong>{selected === account.id && <Check size={17} aria-hidden="true"/>}</span>
            <span className="child-card-count">{account.groups.length === 1 ? 'קבוצה אחת מאושרת' : `${account.groups.length} קבוצות מאושרות`}</span>
            <span className={'child-readiness ' + (!account.groups.length ? 'needs-groups' : '')}>{stale ? 'ממתינים לרענון' : account.ready ? 'מוכן לכניסה בטאבלט' : !account.groups.length ? 'צריך לבחור קבוצות' : 'ממתין לחיבור החשבון'}</span>
            {!!account.groups.length && <span className="child-card-groups">{account.groups.slice(0, 2).map(group => group.title).join(' · ')}{account.groups.length > 2 ? ` · ועוד ${account.groups.length - 2}` : ''}</span>}
            <span className="child-card-link">{selected === account.id ? 'החשבון שנבחר' : 'ניהול קבוצות וחשבון'}<ChevronLeft size={15}/></span>
          </button>
        </li>)}</ul>
      </nav>
      {child ? <ChildWorkspace key={child.id} {...props} data={data} selectedChild={child}/>
        : <p className="child-workspace-empty" role="status">בחרו ילד או ילדה מהרשימה כדי לנהל את החשבון.</p>}
    </div>}
  </section>;
}

function ChildWorkspace({ data, selectedChild: child, busy, stale, createdChild, onGrant, onSettings, onRefresh, photoRetryKey, children }: Props & {
  data: FamilyOverview; selectedChild: ReturnType<typeof familyProgress>['children'][number];
}) {
  const [change, setChange] = useState<(ChildGrantChange & { title: string; phase: 'saving' | 'settled'; acknowledged?: boolean }) | null>(null);
  const guard = useRef(new ChildActionGuard());
  useEffect(() => () => guard.current.invalidate(), []);
  const toggle = async (groupId: string, allowed: boolean) => {
    if (busy || stale) return;
    const request = guard.current.begin(); if (request === null) return;
    const update = { childId: child.id, groupId, allowed, title: data.groups.find(group => group.id === groupId)?.title || '' };
    setChange({ ...update, phase: 'saving' });
    let acknowledged = false;
    try { acknowledged = await onGrant({ childId: update.childId, groupId, allowed }); }
    catch { /* The parent reconciles even when a mutation response is lost. */ }
    if (guard.current.finish(request)) setChange({ ...update, phase: 'settled', acknowledged });
  };
  const confirmed = change && change.phase === 'settled' && confirmedChildGrant(data, change, stale);
  const saving = change?.phase === 'saving';
  return <div className="child-workspace-panel" id="family-groups" role="region" tabIndex={-1} aria-labelledby="selected-child-title">
    <header className="selected-child-heading"><div><span className="eyebrow">מנהלים עכשיו את החשבון של</span><h3 id="selected-child-title" dir="auto">{childLabel(child)}</h3>
      <p>שם לכניסה: <b dir="auto">{child.name}</b></p></div>
      <div className="child-account-actions" role="group" aria-label={`הגדרות החשבון של ${childLabel(child)}`}>
        <button type="button" className="outline-button" disabled={busy || stale || saving} onClick={() => onSettings(child.id, 'profile')}><Settings size={16}/> שינוי שם</button>
        <button type="button" className="text-button" disabled={busy || stale || saving} onClick={() => onSettings(child.id, 'password')}>איפוס סיסמה</button>
      </div>
    </header>
    {createdChild === child.id && <p className="setup-success" role="status">החשבון של <bdi>{child.name}</bdi> נוצר ונבחר. עכשיו בוחרים אילו קבוצות להציג בו.</p>}
    {stale && <p className="child-sync-note" role="status">מציגים את המידע האחרון. שינוי קבוצות והגדרות יתאפשר אחרי אימות מול השרת.</p>}
    <section className="child-groups-section" aria-labelledby="selected-groups-title">
      <div className="child-groups-heading"><div><h4 id="selected-groups-title">הקבוצות של <bdi>{child.name}</bdi></h4><p>{child.groups.length ? `${child.groups.length === 1 ? 'קבוצה אחת מאושרת' : `${child.groups.length} קבוצות מאושרות`} לחשבון הזה` : 'עדיין אין קבוצות מאושרות — בחרו מהרשימה למטה'}</p></div>
        <button type="button" className="icon-button" aria-label="רענון רשימת קבוצות" title="רענון רשימת קבוצות" disabled={busy || stale || saving || data.state !== 'connected' || !!data.disconnect} onClick={onRefresh}><RefreshCw size={18}/></button></div>
      <p className="child-save-help">כל שינוי נשמר בנפרד. האישור חל רק על <bdi>{child.name}</bdi>, ולא על הילדים האחרים.</p>
      <div className={'child-save-feedback ' + (confirmed ? 'confirmed' : '')} role={change?.phase === 'settled' && !stale && !confirmed ? 'alert' : 'status'} aria-live="polite">
        {change ? saving ? `שומרים את השינוי ב־${change.title} עבור ${child.name}…` : stale ? `לא ניתן לאמת את השינוי בקבוצה ${change.title} עבור ${child.name}. ממתינים לחיבור ולרענון לפני שינוי נוסף.`
          : confirmed ? `${change.title} ${change.allowed ? 'אושרה' : 'הוסרה מהקבוצות המאושרות'} עבור ${child.name}.`
          : `לא התקבל אישור לשינוי בקבוצה ${change.title} עבור ${child.name}. הרשימה מציגה את המצב האחרון מהשרת; אפשר לבדוק ולנסות שוב.` : 'הקבוצות המסומנות הן הקבוצות המאושרות לחשבון הזה.'}
      </div>
      <GroupPicker groups={data.groups} grants={data.grants} childId={child.id} childName={child.name} showFilters
        busy={busy || stale || saving} loading={false} onToggle={toggle} photoRetryKey={photoRetryKey}/>
      <div className="small-note child-access-note"><ShieldCheck size={16}/><p>הסרת האישור מבטלת את הגישה של <bdi>{child.name}</bdi> לקבוצה, ואינה משנה את הגישה של הילדים האחרים או של ההורה. עותקים שכבר הורדו למכשיר אינם נמחקים מרחוק.</p></div>
    </section>
    {children && <details className="child-more-settings"><summary>חתימה אוטומטית להודעות של <bdi>{child.name}</bdi></summary>{children}</details>}
  </div>;
}
