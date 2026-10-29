import { compareChatActivity } from './chatOrder';
import { newClientId as newId } from './clientId';
import { useVisibleChatRead } from './useVisibleChatRead';
import { useUsageBudget } from './useUsageBudget';
import { UsageIndicator, UsageOverlay } from './UsageBudget';
import './usage-budget.css';
import { readRoute, writeRoute, type AppRoute } from './browserNavigation';
import { resilientEventStream } from './eventStream';
import { LazyEmojiStickerPicker as EmojiStickerPicker } from './LazyEmojiStickerPicker';
import { insertEmoji, rememberEmoji } from './emojiSelection';
import { loadStickerFile } from './stickerFile';
import './emoji-picker.css';
import './groupAvatar.css';
import { ChatCelebration, useChatCelebration } from './ChatCelebration';
import './chatCelebration.css';
import { HistoryPanel } from './HistoryPanel';
import { MessageImage } from './MessageImage';
import { DisconnectPanel } from './DisconnectPanel';
import { GroupAvatar } from './GroupAvatar';
import { ErrorToast } from './ErrorToast';
import { ChildManagement } from './ChildManagement';
import { ChildTabletSetup } from './ChildTabletSetup';
import { reconcileChildSelection, readChildSelection, saveChildSelection } from './childManagementState';
import { ParentChatFilter, filterParentGroups, searchChatGroups, parentEmptyMessage, type ChatFilter } from './ParentChatFilter';
import { CatalogConversation, ConversationVerification } from './CatalogConversation';
import './parentChatCatalog.css';
import { childLabel } from './childLabels';
import { LatestRequest, isGroupDataRefresh } from './latestRequest';
import { AccountSettings } from './AccountSettings';
import './account-settings.css';
import { FamilySetup } from './FamilySetup';
import { FamilyProgress, familyProgress, type FamilyOverview } from './FamilyProgress';
let setupToken = new URLSearchParams(location.hash.slice(1)).get('setup') || '';
if (setupToken) history.replaceState(null, '', location.pathname + location.search);
import React, { useState, useEffect, useRef, useCallback } from 'react';
import { createRoot } from 'react-dom/client';
import {
  MessageCircleHeart,
  Search,
  LockKeyhole,
  ArrowRight,
  ArrowLeft,
  Send,
  Mic,
  Paperclip,
  Smile,
  Palette,
  ChevronDown,
  MoreHorizontal,
  Check,
  CheckCheck,
  Reply,
  Trash2,
  Pencil,
  X,
  LogOut,
  ShieldCheck,
  Plus,
  WifiOff,
  RefreshCw,
  Download,
  FileText,
  Play,
  Square,
  Sun,
  Moon,
  Flower2,
  Image as ImageIcon,
  Settings,
  LoaderCircle,
  Headphones,
  Volume2,
  Video,
  Sticker,
  ChevronLeft,
  AlertCircle,
  Eye,
  EyeOff,
} from 'lucide-react';
import { api, isTransientApiError, setCsrf } from './api';
import './style.css';
import './child-management.css';
import { DiagnosticsPanel } from './DiagnosticsPanel';
import { UpdatesPanel } from './UpdatesPanel';
import type { PublicMessage } from '../src/service';
import type { Capabilities } from '../src/connectors/types';
type Me = {
  user: {
    id: string;
    familyId: string;
    role: 'parent' | 'child';
    name: string;
  };
  csrf: string;
  connector: 'mock' | 'baileys';
  capabilities: Partial<Capabilities>;
  connection: string;
  idleMs: number | null;
  maxMedia: number;
  senderLabel?: string;
};
type Group = { id: string; title: string; canRead: boolean; last?: PublicMessage; lastMessageTime?: number; unreadCount?: number; kind?: 'group' | 'direct' };
const emojiChoices = ['💚', '❤️', '👍', '😂', '✨', '🌸', '🎉', '🙏'];
const time = (n: number) =>
  new Intl.DateTimeFormat('he-IL', { hour: '2-digit', minute: '2-digit' }).format(n);
const profileIcons: Record<string, typeof Flower2> = { flower: Flower2, heart: MessageCircleHeart, sun: Sun };
function IconButton({
  label,
  children,
  onClick,
  disabled,
  className = '',
}: {
  label: string;
  children: React.ReactNode;
  onClick?: () => void;
  disabled?: boolean;
  className?: string;
}) {
  return (
    <button
      type="button"
      className={'icon-button ' + className}
      title={label}
      aria-label={label}
      disabled={disabled}
      onClick={onClick}
    >
      {children}
    </button>
  );
}
function App() {
  const [me, setMe] = useState<Me | null>(null),
    [loaded, setLoaded] = useState(false),
    [error, setError] = useState('');
  const [route, setRoute] = useState<AppRoute>({ mode: 'chat', group: null });
  const navigate = (next: AppRoute) => {
    if (!me) return;
    writeRoute(history, next, me.user.id);
    setError('');
    setRoute(next);
  };
  useEffect(() => {
    if (!me) return;
    const restore = () => {
      setError('');
      setRoute(readRoute(history.state, me.user.id, me.user.role) ||
        { mode: me.user.role === 'parent' ? 'parent' : 'chat', group: null });
    };
    window.addEventListener('popstate', restore);
    return () => window.removeEventListener('popstate', restore);
  }, [me?.user.id, me?.user.role]);
  const refresh = useCallback(async () => {
    if (localStorage.getItem('yachad-locked') === '1') {
      setLoaded(true);
      setMe(null);
      return;
    }
    try {
      const m = await api<Me>('/me');
      setCsrf(m.csrf);
      setMe(m);
      const next = readRoute(history.state, m.user.id, m.user.role) ||
        { mode: m.user.role === 'parent' ? 'parent' as const : 'chat' as const, group: null };
      writeRoute(history, next, m.user.id, true);
      setRoute(next);
    } catch {
      setMe(null);
    } finally {
      setLoaded(true);
    }
  }, []);
  useEffect(() => {
    void refresh();
    const expire = () => {
      localStorage.setItem('yachad-locked', '1');
      setMe(null);
      setCsrf('');
    };
    window.addEventListener('session-expired', expire);
    return () => window.removeEventListener('session-expired', expire);
  }, [refresh]);
  const logout = useCallback(async () => {
    const attempt = api('/logout', 'POST');
    localStorage.setItem('yachad-locked', '1');
    setMe(null);
    setCsrf('');
    try {
      await attempt;
    } catch {
      setError('המכשיר ננעל מקומית. החיבור לשרת אינו זמין כרגע.');
    }
  }, []);
  useEffect(() => {
    if (!me) return;
    let last = Date.now(),
      sent = 0;
    const onActivity = () => {
      last = Date.now();
      if (Date.now() - sent > 30000) {
        sent = Date.now();
        void api('/activity', 'POST').catch(() => {});
      }
    };
    const check = () => {
      if (me.idleMs && Date.now() - last >= me.idleMs) void logout();
    };
    const timer = setInterval(check, 5000);
    for (const e of ['pointerdown', 'keydown', 'touchstart'])
      window.addEventListener(e, onActivity, { passive: true });
    const visibility = () => {
      if (document.visibilityState === 'visible') check();
    };
    document.addEventListener('visibilitychange', visibility);
    return () => {
      clearInterval(timer);
      for (const e of ['pointerdown', 'keydown', 'touchstart'])
        window.removeEventListener(e, onActivity);
      document.removeEventListener('visibilitychange', visibility);
    };
  }, [me?.user.id, logout]);
  useEffect(() => {
    const handler = (e: StorageEvent) => {
      if (e.key === 'yachad-locked' && e.newValue === '1') {
        setMe(null);
        setCsrf('');
      }
    };
    window.addEventListener('storage', handler);
    return () => window.removeEventListener('storage', handler);
  }, []);
  if (!loaded)
    return (
      <div className="splash">
        <MessageCircleHeart />
        <span>רגע, מתחברים…</span>
      </div>
    );
  if (!me) return <Login onLogin={refresh} />;
  return (
    <div className="app-shell">
      {error && (
        <ErrorToast message={error} onDismiss={() => setError('')} />
      )}
      <div className="app-content">
      {route.mode === 'parent' && me.user.role === 'parent' ? (
        <Parent me={me} logout={logout} openChat={() => navigate({ mode: 'chat', group: null })} onError={setError} />
      ) : (
        <Chat
          key={me.user.id}
          me={me}
          onMe={setMe}
          logout={logout}
          parent={() => navigate({ mode: 'parent', group: null })}
          routeGroup={route.group}
          onSelect={group => navigate({ mode: 'chat', group })}
          onError={setError}
        />
      )}
      </div>
    </div>
  );
}
function Login({ onLogin }: { onLogin: () => Promise<void> }) {
  const [setupDone, setSetupDone] = useState(false);
  const [setupState, setSetupState] = useState('');
  useEffect(() => { void fetch('/api/setup/status').then(r => r.json()).then(x => setSetupState(x.state)).catch(() => {}); }, []);
  const [name, setName] = useState(''),
    [password, setPassword] = useState(''),
    [show, setShow] = useState(false),
    [busy, setBusy] = useState(false),
    [error, setError] = useState('');
  if (setupToken && !setupDone) return <FamilySetup setupToken={setupToken}
    onDone={() => { setupToken = ''; setSetupDone(true); void onLogin(); }}
    onClose={() => { setupToken = ''; setSetupDone(true); }} />;
  return (
    <main className="login-page">
      {setupState === 'ready' && !setupToken && <p className="setup-link-notice" role="status">ההכנה עדיין פתוחה. חזרו לקישור הפרטי שהציג המתקין. רענון הדף מסיר את הקישור הפרטי להגנה על החשבון; אין צורך ליצור משפחה נוספת.</p>}
      <div className="login-art">
        <div className="brand">
          <MessageCircleHeart /> יחד<span>השיחות שלנו</span>
        </div>
        <div className="art-bubble one">
          קצת מילים.
          <br />
          הרבה ביחד.<span>💚</span>
        </div>
        <div className="art-bubble two">
          איזה כיף שנפגשים כאן <Flower2 />
        </div>
        <div className="art-orbit" />
        <p>
          מרחב משפחתי לשיחות, לרגעים
          <br />
          ולכל הדברים הקטנים שביניהם.
        </p>
      </div>
      <form
        className="login-card"
        onSubmit={async (e) => {
          e.preventDefault();
          setBusy(true);
          setError('');
          try {
            await api('/login', 'POST', { name, password });
            setPassword('');
            localStorage.removeItem('yachad-locked');
            await onLogin();
          } catch (e) {
            setError((e as Error).message);
          } finally {
            setBusy(false);
          }
        }}
      >
        <span className="eyebrow">טוב להיפגש</span>
        <h1>נכנסים לשיחות</h1>
        <p>עם פרטי הכניסה האישיים שהוגדרו בבית.</p>
        <label>
          שם
          <input
            dir="auto"
            autoComplete="username"
            required
            maxLength={120}
            value={name}
            onChange={(e) => setName(e.target.value)}
          />
        </label>
        <label>
          סיסמה או קוד
          <div className="password-field">
            <input
              type={show ? 'text' : 'password'}
              autoComplete="current-password"
              autoCapitalize="none"
              autoCorrect="off"
              spellCheck={false}
              dir="auto"
              required
              value={password}
              maxLength={256}
              onChange={(e) => setPassword(e.target.value)}
            />
            <IconButton label={show ? 'הסתרת סיסמה' : 'הצגת סיסמה'} onClick={() => setShow(!show)}>
              {show ? <EyeOff size={18} /> : <Eye size={18} />}
            </IconButton>
          </div>
        </label>
        {error && (
          <p role="alert" className="error">
            {error}
          </p>
        )}
        <button className="primary" disabled={busy}>
          {busy ? (
            <LoaderCircle className="spin" />
          ) : (
            <>
              בואו ניכנס <ArrowLeft size={18} />
            </>
          )}
        </button>
        <div className="login-note">
          <LockKeyhole size={15} /> כניסה אישית · מכשיר משותף? זכרו לנעול ביציאה
        </div>
      </form>
    </main>
  );
}
function Chat({
  me,
  onMe,
  logout,
  parent,
  routeGroup,
  onSelect,
  onError,
}: {
  me: Me;
  onMe: (x: Me) => void;
  logout: () => Promise<void>;
  parent: () => void;
  routeGroup?: string | null;
  onSelect?: (group: string | null) => void;
  onError: (x: string) => void;
}) {
  const budget = useUsageBudget(me.user.role === 'child', me.user.id);
  const [confirmedUsage, setConfirmedUsage] = useState(-1);
  const usageVerifying = me.user.role === 'child' && confirmedUsage !== budget.activation.current;
  const usageSuspended = budget.suspended || usageVerifying;
  const [groups, setGroups] = useState<Group[]>([]),
    [selected, setSelected] = useState<string | null>(routeGroup ?? null),
    [messages, setMessages] = useState<PublicMessage[]>([]),
    [cursor, setCursor] = useState<string | null>(null),
    [busy, setBusy] = useState(true),
    [listBusy, setListBusy] = useState(true),
    [online, setOnline] = useState(false),
    [typing, setTyping] = useState(false),
    [query, setQuery] = useState(''),
    [searchOpen, setSearchOpen] = useState(false),
    [search, setSearch] = useState(''),
    [results, setResults] = useState<PublicMessage[] | null>(null),
    [reply, setReply] = useState<PublicMessage | null>(null),
    [themeOpen, setThemeOpen] = useState(false),
    [theme, setTheme] = useState(() => localStorage.getItem('yachad-theme') || 'mint'),
    [effects, setEffects] = useState(() => localStorage.getItem('yachad-effects') === 'on'),
    [flair, setFlair] = useState(() => localStorage.getItem('yachad-flair') || 'flower'),
    [dark, setDark] = useState(() => localStorage.getItem('yachad-dark') === 'true'),
    [wallpaper, setWallpaper] = useState(() => localStorage.getItem('yachad-wallpaper') || 'dots'),
    [menu, setMenu] = useState<string | null>(null),
    [editing, setEditing] = useState<PublicMessage | null>(null),
    [oldBusy, setOldBusy] = useState(false),
    [newCount, setNewCount] = useState(0),
    [needsRefresh, setNeedsRefresh] = useState(false),
    [stickersOpen, setStickersOpen] = useState(false),
    [attachmentKind, setAttachmentKind] = useState('file'),
    [settingsOpen, setSettingsOpen] = useState(false);
  useEffect(() => { if (routeGroup !== undefined) setSelected(routeGroup); }, [routeGroup]);
  const selectGroup = (group: string | null) => { setSelected(group); onSelect?.(group); };
  const [chatFilter, setChatFilter] = useState<ChatFilter>({ mode: 'all', childId: '' });
  const [filterOverview, setFilterOverview] = useState<FamilyOverview | null>(null);
  const [filterVerified, setFilterVerified] = useState(false);
  const [groupsVerified, setGroupsVerified] = useState(false), [groupsChecking, setGroupsChecking] = useState(false);
  const groupsVerifiedRef = useRef(groupsVerified);
  groupsVerifiedRef.current = groupsVerified;
  const filterRef = useRef(chatFilter), listGeneration = useRef(new LatestRequest()), groupDataGeneration = useRef(new LatestRequest());
  filterRef.current = chatFilter;
  const visibleGroups = me.user.role === 'parent'
    ? filterParentGroups(groups, chatFilter, filterOverview, filterVerified) : groups;
  const listedGroups = searchChatGroups(visibleGroups, query).slice().sort(compareChatActivity);
  const activeSelected = !usageSuspended && groupsVerified && visibleGroups.some(group => group.id === selected && group.canRead) ? selected : null;
  const viewport = useRef<HTMLDivElement>(null),
    bottom = useRef<HTMLDivElement>(null),
    selectedRef = useRef(selected),
    nearBottom = useRef(true),
    requestGeneration = useRef(0),
    typingTimer = useRef<ReturnType<typeof setTimeout> | null>(null),
    seenIds = useRef(new Set<string>()),
    composerPending = useRef(false);
  const composerBusyChanged = useCallback((value: boolean) => { composerPending.current = value; }, []);
  selectedRef.current = activeSelected;
  const verifyingSelection = usageSuspended || !groupsVerified || (me.user.role === 'parent' && chatFilter.mode === 'child' && !filterVerified);
  // Preserve only the local composer while verification is pending, never message content.
  // A confirmed removal/revocation destroys it, as does leaving this Chat/account.
  const composerGroup = (verifyingSelection ? groups : visibleGroups).find(g => g.id === selected && g.canRead);
  const current = visibleGroups.find((g) => g.id === selected),
    c = me.capabilities;
  const invalidateFilter = () => {
    setFilterVerified(false);
    groupsVerifiedRef.current = false;
    setGroupsVerified(false);
    selectedRef.current = null;
    requestGeneration.current++;
  };
  const changeFilter = (next: ChatFilter) => {
    if (next.mode === filterRef.current.mode && next.childId === filterRef.current.childId) return;
    filterRef.current = next;
    selectedRef.current = null;
    requestGeneration.current++;
    selectGroup(null);
    setQuery('');
    setChatFilter(next);
    // Never reuse a previously verified grant snapshot when entering a child view.
    if (next.mode === 'child') void refreshList();
  };
  const refreshList = useCallback(async () => {
    if (budget.suspendedRef.current) return;
    const usageActivation = budget.activation.current;
    const generation = listGeneration.current.begin();
    groupDataGeneration.current.invalidate();
    invalidateFilter();
    setGroupsChecking(true);
    try {
      const [data, overview] = await Promise.all([
        api<Group[]>('/groups'),
        me.user.role === 'parent' ? api<FamilyOverview>('/parent/overview').catch(() => null) : Promise.resolve(null),
      ]);
      if (!listGeneration.current.isCurrent(generation) || budget.suspendedRef.current || usageActivation !== budget.activation.current) return;
      setFilterOverview(overview);
      setFilterVerified(overview !== null);
      setGroups(data);
      setConfirmedUsage(usageActivation);
      groupsVerifiedRef.current = true;
      setGroupsVerified(true);
      setGroupsChecking(false);
      setNeedsRefresh(me.user.role === 'parent' && overview === null);
      setListBusy(false);
      // Content invalidation clears selectedRef before the request. Reconcile
      // the raw selection separately so removed/readded catalog rows cannot reopen.
      setSelected(previous => previous && data.some(group => group.id === previous) ? previous : null);
    } catch (e) {
      if (!listGeneration.current.isCurrent(generation)) return;
      setGroupsChecking(false);
      setListBusy(false);
      setNeedsRefresh(true);
    }
  }, [me.user.id, me.user.role]);
  useEffect(() => {
    if (me.user.role !== 'child') return;
    invalidateFilter();
    listGeneration.current.invalidate(); groupDataGeneration.current.invalidate();
    if (!budget.suspended) void refreshList();
  }, [budget.suspended, me.user.role]);
  // Message-only events cannot change grants. Keep the composer mounted so incoming
  // messages and successful sends do not destroy a parent's unsent draft/attachment.
  const refreshGroupData = useCallback(async () => {
    // A message-only response must not race or bypass a full permission check.
    // Full refresh invalidates older reads; newer ones wait for its confirmation.
    if (!groupsVerifiedRef.current) return;
    const generation = groupDataGeneration.current.begin();
    try {
      const data = await api<Group[]>('/groups');
      if (!groupDataGeneration.current.isCurrent(generation)) return;
      setGroups(data);
    } catch { /* Full permission refresh/reconnect owns recovery and its stale flag. */ }
  }, []);
  useVisibleChatRead(viewport, activeSelected, groupsVerified && !busy && results === null && !settingsOpen && !themeOpen, !!c.read, messages, refreshGroupData);
  const refreshMessages = useCallback(
    async (g: string, initial = false) => {
      const generation = requestGeneration.current;
      try {
        const data = await api<{ messages: PublicMessage[]; cursor: string | null }>(
          '/groups/' + encodeURIComponent(g) + '/messages',
        );
        if (selectedRef.current !== g || generation !== requestGeneration.current) return;
        const added = data.messages.filter((m) => !seenIds.current.has(m.id)).length;
        for (const m of data.messages) seenIds.current.add(m.id);
        setMessages((prev) => {
          const byId = new Map(prev.map((m) => [m.id, m]));
          if (initial) byId.clear();
          for (const m of data.messages) byId.set(m.id, m);
          return [...byId.values()].sort((a, b) => a.time - b.time || a.id.localeCompare(b.id));
        });
        if (initial) setCursor(data.cursor);
        if (initial || nearBottom.current)
          setTimeout(
            () => bottom.current?.scrollIntoView({ behavior: initial ? 'instant' : 'smooth' }),
            20,
          );
        else if (!initial && added > 0) setNewCount((n) => n + added);
        setBusy(false);
        // Local read state is updated only after visible message rows render.
      } catch (e) {
        if (selectedRef.current === g && generation === requestGeneration.current) {
          setBusy(false);
          onError((e as Error).message);
        }
      }
    },
    [c.read, onError],
  );
  useEffect(() => {
    void refreshList();
    const source = resilientEventStream(() => new EventSource('/api/events'));
    let active = true;
    const refreshMe = () => void api<Me>('/me').then(m => {
      if (!active) return;
      setCsrf(m.csrf); onMe(m);
    }).catch(() => {});
    source.addEventListener('ready', () => {
      refreshMe();
      setOnline(true);
      void refreshList();
      const g = selectedRef.current;
      if (g) void refreshMessages(g);
    });
    source.addEventListener('refresh', (event) => {
      if (isGroupDataRefresh((event as MessageEvent).data)) void refreshGroupData();
      else void refreshList();
      refreshMe();
      const g = selectedRef.current;
      if (g) void refreshMessages(g);
    });
    source.addEventListener('expired', () => window.dispatchEvent(new Event('session-expired')));
    source.addEventListener('typing', (e) => {
      if (JSON.parse((e as MessageEvent).data).group === selectedRef.current) {
        setTyping(true);
        if (typingTimer.current) clearTimeout(typingTimer.current);
        typingTimer.current = setTimeout(() => setTyping(false), 6000);
      }
    });
    source.addEventListener('paused', () => setTyping(false));
    source.addEventListener('usage', budget.reconcile);
    source.onerror = () => {
      setOnline(false);
      listGeneration.current.invalidate();
      groupDataGeneration.current.invalidate();
      invalidateFilter();
      setGroupsChecking(false);
    };
    return () => {
      active = false;
      listGeneration.current.invalidate();
      groupDataGeneration.current.invalidate();
      requestGeneration.current++;
      source.close();
      if (typingTimer.current) clearTimeout(typingTimer.current);
    };
  }, [me.user.id]);
  useEffect(() => {
    requestGeneration.current++;
    seenIds.current.clear();
    setMessages([]);
    setResults(null);
    setSearch('');
    setSearchOpen(false);
    setReply(null);
    setEditing(null);
    setMenu(null);
    setTyping(false);
    setNewCount(0);
    setCursor(null);
    nearBottom.current = true;
    if (activeSelected) {
      setBusy(true);
      void refreshMessages(activeSelected, true);
    }
  }, [activeSelected]);
  useEffect(() => {
    localStorage.setItem('yachad-theme', theme);
    localStorage.setItem('yachad-dark', String(dark));
    localStorage.setItem('yachad-wallpaper', wallpaper);
  }, [theme, dark, wallpaper]);
  const action = async (fn: () => Promise<unknown>) => {
    try {
      await fn();
      if (selectedRef.current) await refreshMessages(selectedRef.current);
    } catch (e) {
      onError((e as Error).message);
    }
  };
  const loadOlder = async () => {
    if (!selected || !cursor) return;
    setOldBusy(true);
    const g = selected,
      gen = requestGeneration.current,
      v = viewport.current,
      old = v?.scrollHeight || 0;
    try {
      const data = await api(
        '/groups/' + encodeURIComponent(g) + '/messages?cursor=' + encodeURIComponent(cursor),
      );
      if (g !== selectedRef.current || gen !== requestGeneration.current) return;
      setMessages((prev) => [
        ...data.messages,
        ...prev.filter((m) => !data.messages.some((x: PublicMessage) => x.id === m.id)),
      ]);
      setCursor(data.cursor);
      setTimeout(() => {
        if (v) v.scrollTop += v.scrollHeight - old;
      }, 0);
    } catch (e) {
      onError((e as Error).message);
    } finally {
      setOldBusy(false);
    }
  };
  const find = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!selected) return;
    const g = selected,
      gen = requestGeneration.current;
    try {
      if (!search.trim()) {
        setResults(null);
        return;
      }
      const data = await api(
        '/groups/' + encodeURIComponent(g) + '/search?q=' + encodeURIComponent(search),
      );
      if (g === selectedRef.current && gen === requestGeneration.current) {
        setResults(data.messages);
        if (data.limited) onError('החיפוש הוגבל ל־2,000 ההודעות האחרונות שנשמרו.');
      }
    } catch (e) {
      onError((e as Error).message);
    }
  };
  return (
    <>
    <UsageOverlay reason={budget.reason || (usageVerifying ? 'checking' : '')} status={budget.status} resume={budget.resume} logout={logout}/>
    <main inert={usageSuspended} style={usageSuspended ? { visibility: 'hidden' } : undefined} aria-hidden={usageSuspended || undefined} className={`chat-app theme-${theme} ${effects ? 'playful' : ''} ${dark ? 'dark' : ''} ${current || (verifyingSelection && composerGroup) ? 'has-chat' : ''}`}>
      <aside className="sidebar">
        <header className="side-top">
          <a className="brand" href="/" onClick={(e) => e.preventDefault()}>
            <MessageCircleHeart />
            יחד<span>השיחות שלנו</span>
          </a>
          <IconButton label="מראה אישי" onClick={() => setThemeOpen(true)}>
            <Palette size={21} />
          </IconButton>
        </header>
        {me.user.role === 'child' && <UsageIndicator status={budget.status} paused={usageSuspended}/>}
        <div className="side-title">
          <div>
            <span className="eyebrow">קצת מילים, הרבה ביחד</span>
            <h1>
              השיחות שלי<span className="tiny-flower">✳</span>
            </h1>
          </div>
          <span className="count" aria-label={`${listedGroups.length} שיחות מוצגות`}>{listedGroups.length}</span>
        </div>
        {me.user.role === 'parent' && <ParentChatFilter filter={chatFilter} overview={filterOverview}
          verified={filterVerified} onChange={changeFilter} />}
        <label className="group-search">
          <Search size={18} />
          <input
            type="search"
            aria-label="חיפוש שיחה"
            placeholder="איזו שיחה מחפשים?"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
          {query && <button type="button" className="text-button clear-group-search" onClick={() => setQuery('')}>ניקוי</button>}
        </label>
        <div className="group-list">
          {listBusy ? (
            <div className="list-loading">
              <LoaderCircle className="spin" />
              טוענים שיחות…
            </div>
          ) : (
            listedGroups
              .map((g, i) => {
                return (
                  <button
                    key={g.id}
                    className={'group-row ' + (g.id === selected ? 'active ' : '') + (!g.canRead ? 'catalog-only' : '')}
                    onClick={() => selectGroup(g.id)}
                  >
                    <GroupAvatar groupId={g.id} title={g.title} retryKey={me.connection} className={'avatar av-' + (i % 3)} />
                    <div className="group-info">
                      <div className="group-heading">
                        <b>{g.title}{g.kind && <small className="chat-kind">{g.kind === 'direct' ? 'שיחה פרטית' : 'קבוצה'}</small>}</b>
                        <span>{groupsVerified && g.canRead && g.last ? time(g.last.time) : ''}</span>
                      </div>
                      <div className="group-preview">
                        {!groupsVerified
                          ? 'בודקים את הגישה לתוכן…'
                          : !g.canRead
                          ? 'הגישה לשיחה אינה זמינה כרגע'
                          : g.last?.deleted
                          ? 'הודעה נמחקה'
                          : g.last?.text ||
                            (g.last?.kind === 'audio'
                              ? '🎙 הודעה קולית'
                              : g.last?.kind === 'image'
                                ? 'תמונה'
                                : 'כאן מתחילה השיחה')}
                      </div>
                    </div>
                    {groupsVerified && g.canRead && (g.unreadCount || 0) > 0 &&
                      <span className="chat-unread-badge" aria-label={`${g.unreadCount} הודעות שלא נקראו בחשבון הזה`}>{g.unreadCount! > 99 ? '99+' : g.unreadCount}</span>}
                    {g.id === selected && <span className="selected-dot" />}
                  </button>
                );
              })
          )}
          {!listBusy && !!visibleGroups.length && !listedGroups.length && (
            <div className="empty-list" role="status">
              <Search />
              <h3>לא נמצאו שיחות</h3>
              <p>נסו שם אחר או נקו את החיפוש כדי לחזור לרשימה.</p>
              <button type="button" className="text-button" onClick={() => setQuery('')}>ניקוי חיפוש</button>
            </div>
          )}
          {!listBusy && !visibleGroups.length && (
            <div className="empty-list">
              <ShieldCheck />
              <h3>כאן יופיעו השיחות</h3>
              <p>{me.user.role === 'parent' ? parentEmptyMessage(chatFilter, filterOverview, filterVerified) : 'השיחות שההורה יבחר יופיעו כאן.'}</p>
              <button className="text-button" onClick={() => void refreshList()}>
                רענון
              </button>
            </div>
          )}
        </div>
        <div className="side-footer">
          <div className="safe-note">
            <ShieldCheck size={16} />
            <span>{me.user.role === 'parent' ? 'חשבון הורה · הסינון לא משנה הרשאות' : 'רק השיחות שנבחרו בשבילך'}</span>
          </div>
          <div className="profile-row">
            <div className="profile-avatar" aria-hidden="true">{React.createElement(profileIcons[flair] || Flower2, { size: 24 })}</div>
            <div>
              <b>{me.user.role === 'parent' ? 'חשבון הורה' : 'המרחב שלי'}</b>
              <span>
                {me.connector === 'mock'
                  ? 'סביבת דוגמה · הודעות מדומות'
                  : me.senderLabel
                    ? 'חתימה: ' + me.senderLabel
                    : 'שולחים מהמספר של ההורה'}
              </span>
            </div>
            <IconButton label="הגדרות חשבון" onClick={() => setSettingsOpen(true)}>
              <Settings size={18} />
            </IconButton>
            <IconButton label="נעילה ויציאה" onClick={() => void logout()}>
              <LockKeyhole size={19} />
            </IconButton>
          </div>
          {me.user.role === 'parent' && (
            <button className="parent-shortcut" onClick={parent}>
              ניהול להורה <ChevronLeft size={15} />
            </button>
          )}
        </div>
      </aside>
      <section className="conversation">
        {current?.canRead && groupsVerified ? (
          <>
            <header className="chat-header">
              <IconButton className="back" label="חזרה לשיחות" onClick={() => selectGroup(null)}>
                <ArrowRight />
              </IconButton>
              <GroupAvatar groupId={current.id} title={current.title} retryKey={me.connection} />
              <div className="chat-title">
                <h2>{current.title}</h2>
                <span>
                  {typing ? (
                    <span className="typing-text">
                      מקלידים<span className="typing-dots">•••</span>
                    </span>
                  ) : (
                    <>
                      <span
                        className={
                          'connection-dot ' +
                          (online && me.connection === 'connected' ? '' : 'offline')
                        }
                      />
                      {online && me.connection === 'connected'
                        ? 'מחוברים, אפשר לדבר'
                        : 'ממתינים לחיבור'}
                    </>
                  )}
                </span>
              </div>
              <div className="header-actions">
                <IconButton
                  label="חיפוש בשיחה"
                  onClick={() => {
                    setSearchOpen(!searchOpen);
                    setResults(null);
                  }}
                >
                  <Search size={21} />
                </IconButton>
                <IconButton label="מראה אישי" onClick={() => setThemeOpen(true)}>
                  <Palette size={21} />
                </IconButton>
              </div>
            </header>
            {(!online || me.connection !== 'connected') && (
              <div className="connection-banner">
                <WifiOff size={17} />{' '}
                {me.connection === 'needs_pairing'
                  ? 'ההורה צריך לקשר מחדש את WhatsApp.'
                  : 'החיבור נקטע. מנסים להתחבר מחדש…'}{' '}
                השליחה תתאפשר כשהחיבור יחזור.
              </div>
            )}
            {me.connector === 'mock' && (
              <div className="demo-label">
                <span />
                מצב דוגמה · ההודעות, התגובות והסטטוסים מדומים
              </div>
            )}
            {searchOpen && (
              <form className="message-search" onSubmit={find}>
                <Search size={18} />
                <input
                  aria-label="חיפוש בהודעות"
                  value={search}
                  onChange={(e) => setSearch(e.target.value)}
                  placeholder="מחפשים משהו בשיחה…"
                />
                <button className="small-button">חיפוש</button>
                <IconButton
                  label="סגירת חיפוש"
                  onClick={() => {
                    setSearchOpen(false);
                    setResults(null);
                  }}
                >
                  <X size={18} />
                </IconButton>
              </form>
            )}
            <div
              className={`message-viewport wallpaper-${wallpaper}`}
              ref={viewport}
              onScroll={() => {
                const v = viewport.current!;
                nearBottom.current = v.scrollHeight - v.scrollTop - v.clientHeight < 100;
                if (nearBottom.current) setNewCount(0);
              }}
            >
              <div className="history-note">
                <LockKeyhole size={12} /> המרחב משותף עם ההורה · ההיסטוריה עשויה להיות חלקית
              </div>
              {cursor && results === null && (
                <button
                  className="older-button"
                  disabled={oldBusy}
                  onClick={() => void loadOlder()}
                >
                  {oldBusy ? <LoaderCircle className="spin" size={16} /> : <RefreshCw size={14} />}{' '}
                  הודעות קודמות
                </button>
              )}
              {results === null && <HistoryPanel key={current.id} groupId={current.id} />}
              <div className="day-marker">
                {results !== null ? `תוצאות חיפוש · ${results.length}` : 'השיחה שלנו'}
              </div>
              {busy ? (
                <div className="message-skeleton">
                  <div />
                  <div />
                  <div />
                </div>
              ) : (
                (results || messages).map((m, index) => (
                  <React.Fragment key={m.id}>
                    {index > 0 &&
                      new Date((results || messages)[index - 1].time).toDateString() !==
                        new Date(m.time).toDateString() && (
                        <div className="day-marker">
                          {new Intl.DateTimeFormat('he-IL', {
                            day: 'numeric',
                            month: 'long',
                          }).format(m.time)}
                        </div>
                      )}
                    <Message
                      userId={me.user.id}
                      isParent={me.user.role === 'parent'}
                      message={m}
                      group={current.id}
                      caps={c}
                      menu={menu === m.id}
                      onMenu={() => setMenu(menu === m.id ? null : m.id)}
                      onReply={() => {
                        if (composerPending.current) { onError('ממתינים לסיום הפעולה הנוכחית לפני מעבר לעריכה או מענה.'); return; }
                        setReply(m);
                        setMenu(null);
                      }}
                      onReaction={(emoji) => {
                        setMenu(null);
                        void action(() =>
                          api(
                            `/groups/${encodeURIComponent(current.id)}/messages/${encodeURIComponent(m.id)}/reaction`,
                            'POST',
                            { emoji },
                          ),
                        );
                      }}
                      onEdit={() => {
                        if (composerPending.current) { onError('ממתינים לסיום הפעולה הנוכחית לפני מעבר לעריכה או מענה.'); return; }
                        setEditing(m);
                        setMenu(null);
                      }}
                      onDelete={() => {
                        setMenu(null);
                        if (confirm('למחוק את ההודעה לכולם?'))
                          void action(() =>
                            api(
                              `/groups/${encodeURIComponent(current.id)}/messages/${encodeURIComponent(m.id)}`,
                              'DELETE',
                            ),
                          );
                      }}
                      onRetry={() => {
                        if (confirm('ייתכן שההודעה כבר הגיעה. לנסות לשלוח שוב באותו מזהה?'))
                          void action(() =>
                            api('/groups/' + encodeURIComponent(current.id) + '/messages', 'POST', {
                              clientId: m.clientId,
                              text: m.text,
                            }),
                          );
                      }}
                    />
                  </React.Fragment>
                ))
              )}
              {!busy && !messages.length && (
                <div className="conversation-empty">
                  <Flower2 />
                  <h2>מילה קטנה מתחילה שיחה</h2>
                  <p>אפשר לשלוח את ההודעה הראשונה שלך.</p>
                </div>
              )}
              {results !== null && !results.length && (
                <div className="conversation-empty">לא נמצאו הודעות מתאימות.</div>
              )}
              {typing && (
                <div className="typing-bubble">
                  מקלידים <span>● ● ●</span>
                </div>
              )}
              <div ref={bottom} />
            </div>
            {newCount > 0 && (
              <button
                className="new-messages"
                onClick={() => {
                  bottom.current?.scrollIntoView({ behavior: 'smooth' });
                  setNewCount(0);
                }}
              >
                הודעות חדשות <ChevronDown size={16} />
              </button>
            )}

          </>
        ) : verifyingSelection && selected && groups.some(g => g.id === selected) ? (
          <ConversationVerification title={groups.find(g => g.id === selected)!.title} checking={groupsChecking} onBack={() => selectGroup(null)} onRetry={() => void refreshList()} />
        ) : current ? (
          <CatalogConversation title={current.title} onBack={() => selectGroup(null)} onRetry={() => void refreshList()} />
        ) : (
          <div className="welcome">
            <div className="welcome-emblem">
              <MessageCircleHeart size={66} />
              <span>✦</span>
              <Flower2 />
            </div>
            <span className="eyebrow">המרחב שלנו, בקצב שלנו</span>
            <h1>טוב להיות יחד.</h1>
            <p>
              בוחרים שיחה, שולחים חיוך,
              <br />
              ומשתפים במה שעובר עלינו.
            </p>
            {needsRefresh ? (
              <button className="small-button" onClick={() => void refreshList()}>
                לא הצלחנו לטעון. לנסות שוב
              </button>
            ) : (
              <span className="welcome-foot">
                <ShieldCheck size={16} /> {me.user.role === 'parent' ? 'השיחות מופיעות בצד · הסינון לא משנה הרשאות' : 'השיחות שנבחרו מופיעות בצד'}
              </span>
            )}
            {me.connector === 'mock' && (
              <div className="demo-label">מצב דוגמה · ללא חיבור לחשבון WhatsApp</div>
            )}
          </div>
        )}
        {composerGroup && (
            <Composer
              key={composerGroup.id}
              group={composerGroup.id}
              messages={messages}
              caps={c}
              onBusyChange={composerBusyChanged}
              maxMedia={me.maxMedia}
              effects={effects}
              flair={flair}
              enabled={!!activeSelected && online && me.connection === 'connected'}
              suspended={!activeSelected}
              reply={reply}
              clearReply={() => setReply(null)}
              editing={editing}
              clearEditing={() => setEditing(null)}
              onSent={() => {
                setReply(null);
                setEditing(null);
                nearBottom.current = true;
                void refreshGroupData();
                void refreshMessages(composerGroup.id).then(() => {
                  bottom.current?.scrollIntoView({ behavior: 'smooth' });
                  nearBottom.current = true;
                  setNewCount(0);
                });
              }}
              onError={onError}
            />
        )}
      </section>
      {themeOpen && (
        <div className="modal-backdrop" onClick={() => setThemeOpen(false)}>
          <section
            role="dialog"
            aria-modal="true"
            aria-label="המראה שלי"
            className="modal theme-panel"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="modal-top">
              <h2>
                קצת יותר שלך <Palette size={23} />
              </h2>
              <IconButton label="סגירה" onClick={() => setThemeOpen(false)}>
                <X />
              </IconButton>
            </div>
            <p>אותן שיחות, האווירה שאת אוהבת.</p>
            <label>צבע שמרגיש נכון</label>
            <div className="theme-options">
              {[
                ['mint', 'מרווה'],
                ['lilac', 'לילך'],
                ['peach', 'אפרסק'],
              ].map(([id, title]) => (
                <button
                  key={id}
                  className={`theme-option ${id} ${theme === id ? 'chosen' : ''}`}
                  aria-pressed={theme === id}
                  onClick={() => setTheme(id)}
                >
                  <span>{theme === id && <Check size={20} />}</span>
                  {title}
                </button>
              ))}
            </div>
            <label>רקע לשיחה</label>
            <div className="segmented">
              {[
                ['dots', 'נקודות'],
                ['plain', 'נקי'],
                ['petals', 'פריחה'],
              ].map(([id, title]) => (
                <button
                  key={id}
                  className={wallpaper === id ? 'active' : ''}
                  aria-pressed={wallpaper === id}
                  onClick={() => setWallpaper(id)}
                >
                  {title}
                </button>
              ))}
            </div>
            <div className="profile-flairs" role="group" aria-label="הסמל האישי שלי">{Object.entries(profileIcons).map(([id, Icon]) => <button key={id} type="button" aria-label={({flower:'פרח',heart:'לב',sun:'שמש'} as Record<string,string>)[id]} aria-pressed={flair === id} onClick={() => { setFlair(id); localStorage.setItem('yachad-flair',id); }}><Icon size={26}/></button>)}</div>
            <label className="secret-toggle"><input type="checkbox" checked={effects} onChange={e => { setEffects(e.target.checked); localStorage.setItem('yachad-effects', e.target.checked ? 'on' : 'off'); }}/> קצת קסם בתנועה</label>
            <p className="muted">תנועות עדינות בבחירת שיחה ובשליחה. הגדרת הפחתת תנועה במכשיר תמיד קודמת לבחירה הזאת.</p>
            <button className="dark-toggle" aria-pressed={dark} onClick={() => setDark(!dark)}>
              {dark ? <Moon size={20} /> : <Sun size={20} />} מצב כהה{' '}
              <span className={'switch ' + (dark ? 'on' : '')} />
            </button>
            <button className="primary" onClick={() => setThemeOpen(false)}>
              בדיוק ככה <Check size={18} />
            </button>
          </section>
        </div>
      )}
      {settingsOpen && <AccountSettings onClose={() => setSettingsOpen(false)} />}
    </main>
    </>
  );
}
function Message({
  userId,
  isParent,
  message: m,
  group,
  caps,
  menu,
  onMenu,
  onReply,
  onReaction,
  onEdit,
  onDelete,
  onRetry,
}: {
  userId: string;
  isParent: boolean;
  message: PublicMessage;
  group: string;
  caps: Partial<Capabilities>;
  menu: boolean;
  onMenu: () => void;
  onReply: () => void;
  onReaction: (e: string) => void;
  onEdit: () => void;
  onDelete: () => void;
  onRetry: () => void;
}) {
  const url = m.mediaId
    ? `/api/groups/${encodeURIComponent(group)}/media/${encodeURIComponent(m.mediaId)}`
    : null;
  const statusTitle: Record<string, string> = {
    sent: 'השרת של WhatsApp אישר שליחה',
    pending: 'ממתינה לאישור',
    delivered: 'החיבור דיווח על מסירה',
    read: 'החיבור דיווח על קריאה — לא בהכרח כל חברי השיחה',
    failed: 'לא התקבל אישור — ייתכן שההודעה הגיעה',
    unknown: 'מצב מסירה אינו ידוע',
  };
  return (
    <article data-local-unread-id={!m.fromMe && !m.deleted ? m.id : undefined} className={'message-row ' + (m.fromMe ? 'outgoing' : 'incoming')}>
      <div className={'bubble ' + (m.kind === 'sticker' ? 'sticker-bubble' : '')}>
        <div className="bubble-top">
          {!m.fromMe && <b className="sender">{m.sender}</b>}
          {!m.deleted && (
            <IconButton className="message-menu-button" label="פעולות בהודעה" onClick={onMenu}>
              <MoreHorizontal size={17} />
            </IconButton>
          )}
        </div>
        {m.reply && !m.deleted && (
          <div className="quote">
            <b>{m.reply.sender}</b>
            <span dir="auto">{m.reply.text}</span>
          </div>
        )}
        {m.deleted ? (
          <p className="deleted">ההודעה נמחקה</p>
        ) : (
          <>
            {url && (m.kind === 'image' || m.kind === 'sticker') && (
              <MessageImage key={url} url={url} sticker={m.kind==='sticker'} alt={m.kind==='sticker'?'מדבקה':m.text||'תמונה שנשלחה בשיחה'}/>
            )}
            {url && m.kind === 'video' && (
              <video controls preload="metadata" playsInline src={url} />
            )}
            {url && m.kind === 'audio' && (
              <div className="voice-message">
                <div className="voice-icon">
                  <Mic size={21} />
                </div>
                <audio controls preload="metadata" src={url} />
              </div>
            )}
            {url && m.kind === 'document' && (
              <a className="document" href={url} download={m.filename}>
                <div>
                  <FileText size={25} />
                </div>
                <span>
                  <b>{m.filename || 'קובץ מצורף'}</b>
                  <small>מסמך · לחיצה להורדה</small>
                </span>
                <Download size={19} />
              </a>
            )}
            {!url && m.kind !== 'text' && m.kind !== 'unsupported' && (
              <p className="media-missing">
                <AlertCircle size={15} /> המדיה לא נשמרה או שכבר אינה זמינה
              </p>
            )}
            {!url&&!m.text&&(m.kind==='text'||m.kind==='unsupported')&&<p className="media-missing" role="status">תוכן ההודעה אינו זמין בתצוגה הזאת.</p>}
            {m.text && (
              <p className="message-text" dir="auto">
                {m.text}
              </p>
            )}
          </>
        )}
        <div className="bubble-meta">
          {m.edited && <span>נערכה</span>}
          <time dateTime={new Date(m.time).toISOString()}>{time(m.time)}</time>
          {m.fromMe && (
            <span
              title={statusTitle[m.status]}
              aria-label={statusTitle[m.status]}
              className={'receipt ' + m.status}
            >
              {m.status === 'read' || m.status === 'delivered' ? (
                <CheckCheck size={15} />
              ) : m.status === 'sent' ? (
                <Check size={15} />
              ) : m.status === 'pending' ? (
                <LoaderCircle className="spin" size={12} />
              ) : (
                <AlertCircle size={13} />
              )}
            </span>
          )}
          {m.status === 'failed' && m.kind === 'text' && (
            <button className="retry" onClick={onRetry}>
              ניסיון נוסף
            </button>
          )}
        </div>
        {m.reactions.length > 0 && (
          <div className="reactions">
            {m.reactions.map((r) => (
              <button
                key={r.emoji}
                disabled={!caps.reactions}
                className={r.mine ? 'mine' : ''}
                aria-label={`תגובה ${r.emoji}, ${r.count}`}
                onClick={() => onReaction(r.mine ? '' : r.emoji)}
              >
                {r.emoji}
                {r.count > 1 && <small>{r.count}</small>}
              </button>
            ))}
          </div>
        )}
        {menu && (
          <div className="message-actions">
            <div className="reaction-picker">
              {caps.reactions &&
                emojiChoices.map((e) => (
                  <button aria-label={'תגובה ' + e} key={e} onClick={() => onReaction(e)}>
                    {e}
                  </button>
                ))}
            </div>
            {caps.reply && (
              <button onClick={onReply}>
                <Reply size={16} />
                מענה
              </button>
            )}
            {m.fromMe &&
              (isParent || m.authorUserId === userId) &&
              m.kind === 'text' &&
              caps.edit &&
              Date.now() - m.time < (caps.editWindowMs || 0) && (
                <button onClick={onEdit}>
                  <Pencil size={16} />
                  עריכה
                </button>
              )}
            {m.fromMe && (isParent || m.authorUserId === userId) && caps.delete && (
              <button className="danger-text" onClick={onDelete}>
                <Trash2 size={16} />
                מחיקה לכולם
              </button>
            )}
            <button onClick={onMenu}>
              <X size={16} />
              סגירה
            </button>
          </div>
        )}
      </div>
    </article>
  );
}
function Composer({
  group,
  messages,
  caps,
  maxMedia,
  effects,
  flair,
  enabled,
  suspended,
  reply,
  clearReply,
  editing,
  clearEditing,
  onSent,
  onError,
  onBusyChange,
}: {
  group: string;
  messages: PublicMessage[];
  caps: Partial<Capabilities>;
  maxMedia: number;
  effects: boolean;
  flair: string;
  enabled: boolean;
  suspended: boolean;
  reply: PublicMessage | null;
  clearReply: () => void;
  editing: PublicMessage | null;
  clearEditing: () => void;
  onSent: () => void;
  onBusyChange: (busy: boolean) => void;
  onError: (x: string) => void;
}) {
  const [text, setText] = useState(''),
    [busy, setBusy] = useState(false),
    [emojiOpen, setEmojiOpen] = useState(false),
    [recentEmojis, setRecentEmojis] = useState<string[]>([]),
    [favoriteStickerIds, setFavoriteStickerIds] = useState<string[]>([]),
    [attachOpen, setAttachOpen] = useState(false),
    [file, setFile] = useState<File | null>(null),
    [sticker, setSticker] = useState(false),
    [recording, setRecording] = useState(false),
    [voice, setVoice] = useState<Blob | null>(null),
    [seconds, setSeconds] = useState(0),
    [preview, setPreview] = useState(''),
    [stickerPreview, setStickerPreview] = useState('');
  const input = useRef<HTMLTextAreaElement>(null),
    fileInput = useRef<HTMLInputElement>(null),
    fileIsSticker = useRef(false),
    recorder = useRef<MediaRecorder | null>(null),
    mediaStream = useRef<MediaStream | null>(null),
    chunks = useRef<Blob[]>([]),
    id = useRef(newId()),
    typingAt = useRef(0),
    mounted = useRef(true),
    cancelRecord = useRef(false),
    stickerRequest = useRef<AbortController | null>(null),
    submitting = useRef(false),
    copiedEdit = useRef(false),
    available = useRef(enabled && !suspended);
  available.current = enabled && !suspended;
  const { burst, celebrate } = useChatCelebration(effects, flair, group);
  const path = '/groups/' + encodeURIComponent(group);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      onBusyChange(false);
      stickerRequest.current?.abort();
      cancelRecord.current = true;
      recorder.current?.state === 'recording' && recorder.current.stop();
      mediaStream.current?.getTracks().forEach((t) => t.stop());
    };
  }, []);
  useEffect(() => {
    if (suspended) {
      if (editing || copiedEdit.current) setText(''); // Never retain copied message content through permission checks.
      stopRecording();
      setEmojiOpen(false); setAttachOpen(false);
    }
  }, [suspended]);
  useEffect(() => {
    if (editing) {
      copiedEdit.current = true;
      setText(
        editing.authorLabel && editing.text.startsWith(editing.authorLabel + ': ')
          ? editing.text.slice(editing.authorLabel.length + 2)
          : editing.text,
      );
      setFile(null);
      setSticker(false);
      setVoice(null);
      input.current?.focus();
    } else if (copiedEdit.current) {
      copiedEdit.current = false;
      setText('');
    }
  }, [editing]);
  useEffect(() => {
    if (reply) input.current?.focus();
  }, [reply]);
  useEffect(() => {
    if (!text && input.current) input.current.style.height = 'auto';
  }, [text]);
  useEffect(() => {
    if (!recording) return;
    const start = Date.now();
    const timer = setInterval(() => {
      const s = Math.floor((Date.now() - start) / 1000);
      setSeconds(s);
      if (s >= 120) stopRecording();
    }, 250);
    return () => clearInterval(timer);
  }, [recording]);
  useEffect(() => {
    if (!voice) {
      setPreview('');
      return;
    }
    const url = URL.createObjectURL(voice);
    setPreview(url);
    return () => URL.revokeObjectURL(url);
  }, [voice]);
  useEffect(() => {
    if (!file || !sticker) { setStickerPreview(''); return; }
    const url = URL.createObjectURL(file); setStickerPreview(url);
    return () => URL.revokeObjectURL(url);
  }, [file, sticker]);
  useEffect(() => { stickerRequest.current?.abort(); }, [enabled, editing, caps.sticker]);
  const availableStickers = [...new Map(messages.filter(m => m.groupId === group && m.kind === 'sticker' && !m.deleted && m.mediaId)
    .map(m => [m.mediaId!, { id: m.mediaId!, url: `/api/groups/${encodeURIComponent(group)}/media/${encodeURIComponent(m.mediaId!)}`, label: m.text || `מדבקה מאת ${m.sender}` }])).values()];
  const chooseSticker = async (mediaId: string) => {
    if (!enabled || busy || editing || !caps.sticker || !availableStickers.some(s => s.id === mediaId)) return;
    const controller = new AbortController();
    stickerRequest.current?.abort(); stickerRequest.current = controller;
    setEmojiOpen(false); setBusy(true); onBusyChange(true);
    try {
      const chosen = await loadStickerFile(availableStickers.find(s => s.id === mediaId)!, maxMedia, controller.signal);
      if (!mounted.current || controller.signal.aborted) return;
      setFile(chosen); setVoice(null); setSticker(true); id.current = newId();
    } catch (e) {
      if (mounted.current && !controller.signal.aborted) onError((e as Error).message);
    } finally { if (mounted.current && stickerRequest.current === controller) { setBusy(false); onBusyChange(false); } }
  };
  const send = async (e?: React.FormEvent) => {
    e?.preventDefault();
    if (submitting.current || busy || !enabled || recording || (!text.trim() && !file && !voice)) return;
    submitting.current = true; setBusy(true); onBusyChange(true);
    try {
      if (editing) {
        await api(`${path}/messages/${encodeURIComponent(editing.id)}`, 'PATCH', { text });
      } else if (file || voice) {
        const form = new FormData();
        form.set('clientId', id.current);
        form.set('text', text);
        if (reply) form.set('replyId', reply.id);
        if (sticker) form.set('sticker', 'true');
        if (voice) form.set('voice', 'true');
        form.set('file', (voice || file)!, voice ? 'voice.webm' : file!.name);
        await api(path + '/media', 'POST', form);
      } else
        await api(path + '/messages', 'POST', { clientId: id.current, text, replyId: reply?.id });
      if (!mounted.current) return;
      setText('');
      setFile(null);
      setVoice(null);
      setSticker(false);
      id.current = newId();
      if (!editing) celebrate();
      onSent();
    } catch (e) {
      if (mounted.current) onError((e as Error).message);
    } finally {
      submitting.current = false;
      if (mounted.current) { setBusy(false); onBusyChange(false); }
    }
  };
  const stopRecording = () => {
    recorder.current?.state === 'recording' && recorder.current.stop();
    mediaStream.current?.getTracks().forEach((t) => t.stop());
    setRecording(false);
  };
  const record = async () => {
    if (!available.current) return;
    if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
      onError('להקלטה צריך כתובת HTTPS שהמכשיר סומך עליה, או localhost.');
      return;
    }
    if (!window.MediaRecorder) {
      onError('הדפדפן הזה אינו תומך בהקלטה.');
      return;
    }
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      if (!mounted.current || !available.current) {
        stream.getTracks().forEach((t) => t.stop());
        return;
      }
      mediaStream.current = stream;
      const mime = ['audio/webm;codecs=opus', 'audio/mp4', 'audio/ogg;codecs=opus'].find((x) =>
        MediaRecorder.isTypeSupported(x),
      );
      const r = new MediaRecorder(stream, mime ? { mimeType: mime } : undefined);
      recorder.current = r;
      chunks.current = [];
      cancelRecord.current = false;
      r.ondataavailable = (e) => {
        if (e.data.size) chunks.current.push(e.data);
        if (chunks.current.reduce((n, b) => n + b.size, 0) > maxMedia) {
          cancelRecord.current = true;
          stopRecording();
          onError('ההקלטה הגיעה למגבלת הגודל.');
        }
      };
      r.onstop = () => {
        stream.getTracks().forEach((t) => t.stop());
        if (mounted.current && !cancelRecord.current)
          setVoice(new Blob(chunks.current, { type: r.mimeType }));
      };
      r.onerror = () => {
        cancelRecord.current = true;
        stopRecording();
        onError('ההקלטה הופסקה. אפשר לנסות שוב.');
      };
      setSeconds(0);
      setRecording(true);
      r.start(500);
    } catch (e) {
      mediaStream.current?.getTracks().forEach((t) => t.stop());
      onError(
        (e as DOMException).name === 'NotAllowedError'
          ? 'אין הרשאה למיקרופון. אפשר לאשר אותה בהגדרות הדפדפן ולנסות שוב.'
          : 'לא הצלחנו להפעיל את המיקרופון. בדקו שהוא פנוי וזמין.',
      );
    }
  };
  return (
    <div className="composer-wrap" hidden={suspended}>
      <ChatCelebration burst={burst} />
      {reply && !editing && (
        <div className="reply-preview">
          <Reply size={18} />
          <div>
            <b>בתשובה ל־{reply.sender}</b>
            <span>{reply.text || 'קובץ מצורף'}</span>
          </div>
          <IconButton label="ביטול מענה" disabled={busy} onClick={clearReply}>
            <X size={17} />
          </IconButton>
        </div>
      )}
      {editing && (
        <div className="reply-preview">
          <Pencil size={18} />
          <b>עריכת הודעה</b>
          <IconButton
            label="ביטול עריכה"
            disabled={busy}
            onClick={() => {
              clearEditing();
              setText('');
            }}
          >
            <X size={17} />
          </IconButton>
        </div>
      )}
      {file && (
        <div className="attachment-preview">
          {stickerPreview ? <img src={stickerPreview} alt="תצוגה מקדימה של המדבקה לפני שליחה" width={72} height={72} style={{ objectFit: 'contain' }} /> : <Paperclip size={18} />}
          {file.name}
          <span>{(file.size / 1024 ** 2).toFixed(1)} MB</span>
          <IconButton label="הסרת קובץ" disabled={busy} onClick={() => { setFile(null); setSticker(false); }}>
            <X size={17} />
          </IconButton>
        </div>
      )}
      {voice && (
        <div className="attachment-preview">
          <Mic size={18} />
          <audio controls src={preview} />
          <IconButton label="מחיקת הקלטה" disabled={busy} onClick={() => setVoice(null)}>
            <Trash2 size={18} />
          </IconButton>
        </div>
      )}
      {emojiOpen && <EmojiStickerPicker recentEmojis={recentEmojis}
        favoriteStickerIds={favoriteStickerIds}
        onFavoriteStickerToggle={id => setFavoriteStickerIds(previous => previous.includes(id) ? previous.filter(value => value !== id) : [...previous, id].slice(-100))}
        onClose={() => setEmojiOpen(false)}
        onEmoji={emoji => {
          if (!enabled || busy || voice) return;
          const inserted = insertEmoji(text, emoji, input.current?.selectionStart ?? null, input.current?.selectionEnd ?? null);
          if (inserted.text.length > 12000) return;
          setText(inserted.text); setRecentEmojis(previous => rememberEmoji(previous, emoji));
          id.current = newId(); setEmojiOpen(false);
          requestAnimationFrame(() => { input.current?.focus(); input.current?.setSelectionRange(inserted.caret, inserted.caret); });
        }}
        stickers={!editing && caps.sticker ? availableStickers : []}
        onSticker={!editing && caps.sticker ? item => void chooseSticker(item.id) : undefined}
        onUploadSticker={!editing && caps.sticker ? () => {
          if (!enabled || busy) return;
          fileIsSticker.current = true; if (fileInput.current) fileInput.current.accept = '.webp';
          fileInput.current?.click(); setEmojiOpen(false);
        } : undefined} />}
      {attachOpen && (
        <div className="composer-popover attach-menu">
          {(caps.image || caps.video || caps.document) && (
            <button
              onClick={() => {
                fileIsSticker.current = false;
                if (fileInput.current) fileInput.current.accept = '*/*';
                fileInput.current?.click();
                setAttachOpen(false);
              }}
            >
              <Paperclip size={19} />
              תמונה, סרטון או מסמך
            </button>
          )}
          {caps.sticker && (
            <button
              onClick={() => {
                fileIsSticker.current = true;
                if (fileInput.current) fileInput.current.accept = '.webp';
                fileInput.current?.click();
                setAttachOpen(false);
              }}
            >
              <Sticker size={19} />
              מדבקה מקובץ WebP
            </button>
          )}
          <small>עד {Math.round(maxMedia / 1024 ** 2)} MB לקובץ</small>
        </div>
      )}
      <input
        type="file"
        hidden
        ref={fileInput}
        accept="*/*"
        onChange={(e) => {
          const f = e.target.files?.[0];
          if (f) {
            if (f.size > maxMedia) onError('הקובץ גדול מהמותר. בחרו קובץ קטן יותר.');
            else {
              setFile(f);
              setSticker(fileIsSticker.current);
              setVoice(null);
              id.current = newId();
            }
          }
          e.target.value = '';
          e.target.accept = '*/*';
        }}
      />
      <form className="composer" onSubmit={send}>
        {recording ? (
          <div className="recording">
            <span className="recording-dot" />
            מקליטים{' '}
            <b dir="ltr">
              {Math.floor(seconds / 60)}:{String(seconds % 60).padStart(2, '0')}
            </b>
            <span className="waveform">▂ ▆ ▃ █ ▅ ▂ ▇ ▃</span>
            <IconButton
              label="ביטול הקלטה"
              onClick={() => {
                cancelRecord.current = true;
                stopRecording();
              }}
            >
              <Trash2 size={20} />
            </IconButton>
            <button type="button" className="stop-record" onClick={stopRecording}>
              <Square size={15} />
              סיום
            </button>
          </div>
        ) : (
          <>
            <IconButton
              label="אימוג׳ים"
              disabled={!enabled || busy}
              onClick={() => {
                setEmojiOpen(!emojiOpen);
                setAttachOpen(false);
              }}
            >
              <Smile size={23} />
            </IconButton>
            <textarea
              ref={input}
              aria-label={editing ? 'עריכת ההודעה' : 'כתיבת הודעה'}
              dir="auto"
              rows={1}
              placeholder={enabled ? 'משהו קטן לשתף…' : 'ממתינים לחיבור…'}
              maxLength={12000}
              disabled={!enabled || busy || !!voice}
              value={text}
              onChange={(e) => {
                setText(e.target.value);
                id.current = newId();
                e.target.style.height = 'auto';
                e.target.style.height = Math.min(130, e.target.scrollHeight) + 'px';
                if (caps.typing && Date.now() - typingAt.current > 4000) {
                  typingAt.current = Date.now();
                  void api(path + '/typing', 'POST', { active: !!e.target.value }).catch(() => {});
                }
              }}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
                  e.preventDefault();
                  void send();
                }
              }}
            />
            {!editing && (caps.image || caps.video || caps.document || caps.sticker) && (
              <IconButton
                label="צירוף קובץ"
                disabled={!enabled || busy || !!voice}
                onClick={() => {
                  setAttachOpen(!attachOpen);
                  setEmojiOpen(false);
                }}
              >
                <Paperclip size={22} />
              </IconButton>
            )}
            {text.trim() || file || voice || editing ? (
              <button
                className="send-button"
                aria-label={editing ? 'שמירת עריכה' : 'שליחת הודעה'}
                disabled={busy || !enabled}
              >
                {busy ? (
                  <LoaderCircle className="spin" size={21} />
                ) : editing ? (
                  <Check size={23} />
                ) : (
                  <Send size={21} />
                )}
              </button>
            ) : caps.audio ? (
              <IconButton
                className="mic-button"
                label="הקלטת הודעה קולית"
                disabled={!enabled || busy}
                onClick={() => void record()}
              >
                <Mic size={23} />
              </IconButton>
            ) : null}
          </>
        )}
      </form>
      <div className="composer-foot">
        <span>אפשר פשוט להיות בקשר.</span>
        <span>
          <LockKeyhole size={10} /> נועלים כשיוצאים
        </span>
      </div>
    </div>
  );
}
function Parent({
  me,
  logout,
  openChat,
  onError,
}: {
  me: Me;
  logout: () => Promise<void>;
  openChat: () => void;
  onError: (x: string) => void;
}) {
  const [groupPhotoRevision, setGroupPhotoRevision] = useState(0);
  const [createdChild, setCreatedChild] = useState('');
  const [settingsChild, setSettingsChild] = useState('');
  const [settingsMode, setSettingsMode] = useState<'profile' | 'password'>('profile');
  const [overviewStale, setOverviewStale] = useState(false);
  const [data, setData] = useState<FamilyOverview | null>(null),
    [qr, setQr] = useState<string | null>(null),
    [busy, setBusy] = useState(false),
    [child, setChild] = useState(() => readChildSelection(me.user.id)),
    [settings, setSettings] = useState(false),
    [addingChild, setAddingChild] = useState(false),
    [deleteOpen, setDeleteOpen] = useState(false),
    [password, setPassword] = useState(''),
    [confirmError, setConfirmError] = useState('');
  useEffect(() => { if (data) saveChildSelection(me.user.id, child); }, [me.user.id, child, !!data]);
  const acting = useRef(false), overviewRequest = useRef(0);
  const load = useCallback(async (background = false) => {
    const request = ++overviewRequest.current;
    try {
      const d = await api<FamilyOverview>('/parent/overview');
      if (request !== overviewRequest.current) return;
      setData(d);
      setChild(prev => reconcileChildSelection(d, prev));
      if (d.state === 'qr') {
        const q = await api('/parent/qr');
        if (request !== overviewRequest.current) return;
        setQr(q.qr);
      } else setQr(null);
      setOverviewStale(false);
    } catch (e) {
      if (request !== overviewRequest.current) return;
      setOverviewStale(true);
      if (background && isTransientApiError(e)) return;
      else onError((e as Error).message);
    }
  }, [onError]);
  useEffect(() => {
    void load();
    const source = resilientEventStream(() => new EventSource('/api/events'));
    source.addEventListener('ready', () => void load(true));
    source.addEventListener('refresh', () => void load(true));
    source.addEventListener('expired', () => window.dispatchEvent(new Event('session-expired')));
    const timer = setInterval(() => void load(true), 10000);
    return () => {
      overviewRequest.current++;
      source.close();
      clearInterval(timer);
    };
  }, []);
  const act = async (path: string, body?: unknown, method = 'POST') => {
    if (acting.current) return false;
    acting.current = true; setBusy(true);
    try {
      await api(path, method, body);
      if (path === '/parent/sync-groups') setGroupPhotoRevision(value => value + 1);
      // If an SSE/timer read supersedes this refresh, keep edits blocked until
      // the newest read confirms the persisted state (even after busy clears).
      setOverviewStale(true);
      await load(path === '/parent/grant');
      return true;
    } catch (e) {
      if (path !== '/parent/grant') onError((e as Error).message);
      // A lost mutation response may still have committed. Reconcile before another click.
      setOverviewStale(true);
      await load(true);
      return false;
    } finally {
      acting.current = false; setBusy(false);
    }
  };
  const selectedChild = data ? familyProgress(data).children.find(item => item.id === child) : undefined;
  const stateLabels: Record<string, string> = {
    connected: 'מחובר',
    connecting: 'מתחברים…',
    reconnecting: 'מנסים להתחבר מחדש',
    qr: 'ממתין לסריקה',
    idle: 'טרם קושר',
    needs_pairing: 'נדרש קישור מחדש',
    conflict: 'החיבור הוחלף — בדקו מכשירים מקושרים',
  };
  return (
    <main className="parent-page">
      <header className="parent-nav">
        <div className="brand">
          <MessageCircleHeart /> יחד <span>המרחב להורה</span>
        </div>
        <div>
          <button className="text-button" disabled={busy || overviewStale} onClick={() => { setSettingsChild(''); setSettingsMode('password'); setSettings(true); }}>
            <Settings size={17} />
            החשבון שלי
          </button>
          <IconButton label="יציאה" onClick={() => void logout()}>
            <LogOut size={20} />
          </IconButton>
        </div>
      </header>
      <div className="parent-content">
        <span className="eyebrow">יחד · ניהול המשפחה</span>
        <h1>מרחב משלהם.<br /><span>בחירה שלכם.</span></h1>
        <p className="parent-intro">כל הילדים, השיחות וההגדרות — במקום אחד.</p>
        {overviewStale && <p className="diagnostics-caption" role="status">עדכון המסך ממתין לחיבור לשרת. ננסה שוב אוטומטית; המידע שכבר מוצג עשוי להיות לא מעודכן. שינויים חסומים עד לאימות המצב.</p>}
        {me.connector === 'mock' && (
          <div className="mock-notice">
            <Flower2 size={21} />
            <div>
              <b>אתם בסביבת דוגמה בטוחה</b>
              <p>אין חיבור ל־WhatsApp. כל השיחות והמדיה כאן הן דוגמאות מקומיות.</p>
            </div>
          </div>
        )}
        {data && familyProgress(data).completed < 3 && <FamilyProgress data={data} busy={busy} stale={overviewStale} onAdd={() => setAddingChild(true)} onSelect={id => { setChild(id); setCreatedChild(''); }} />}
        <ChildManagement data={data} selected={child} busy={busy} stale={overviewStale} createdChild={createdChild}
          photoRetryKey={`${data?.state || 'idle'}:${groupPhotoRevision}`}
          onAdd={() => setAddingChild(true)} onSelect={id => { setChild(id); setCreatedChild(''); }}
          onSettings={(id, mode) => { setSettingsChild(id); setSettingsMode(mode); setSettings(true); }}
          onRetry={() => void load()} onRefresh={() => void act('/parent/sync-groups')}
          onAccess={change => act('/parent/child-access', change, 'PUT')}
          onGrant={change => { setCreatedChild(''); return act('/parent/grant', change, 'PUT'); }}>
          <IdentitySettings key={child} childId={child} disabled={busy || overviewStale}
            value={data?.children.find(account => account.id === child)?.senderLabel || ''}
            onSave={async label => {
              const saved = await act('/parent/identity', { childId: child, label }, 'PUT');
              if (!saved) throw new Error('לא התקבל אישור לשמירת החתימה. בדקו את המצב לאחר הרענון.');
            }} onError={onError}/>
        </ChildManagement>
        <details className="parent-connection-details" open={data?.state !== 'connected' || !!data?.disconnect}>
          <summary>חיבור החשבון <span>{data?.state === 'connected' && !data?.disconnect ? 'מחובר' : 'נדרשת בדיקה'}</span></summary>
          <section className="parent-card" id="family-connection" tabIndex={-1}>
            <div className="step-title">
              <span>01</span>
              <h2>מחברים את WhatsApp</h2>
              <span className={'state-pill ' + (data?.state === 'connected' ? 'connected' : '')}>
                {(data?.disconnect ? 'מנותק' : stateLabels[data?.state || '']) || 'טוענים…'}
              </span>
            </div>
            <p>חשבון ההורה משמש לשליחה ולקבלה. פרטי הקישור נשארים בשרת.</p>
            {qr ? (
              <div className="qr">
                <img src={qr} alt="קוד QR לקישור WhatsApp — להורה בלבד" />
                <p>WhatsApp בטלפון ← מכשירים מקושרים ← קישור מכשיר</p>
              </div>
            ) : (
              <div className="link-illustration">
                <div className="phone-frame">
                  <MessageCircleHeart size={35} />
                  <span>WhatsApp</span>
                </div>
                <span className="link-dots">•••••</span>
                <div className="tablet-frame">
                  <Flower2 size={36} />
                  <span>יחד</span>
                </div>
              </div>
            )}
            {data?.state === 'connected' ? (
              <div className="connected-note">
                <CheckCheck size={20} />{' '}
                {me.connector === 'mock' ? 'המתאם המדומה פעיל' : 'החשבון מחובר'}
              </div>
            ) : (
              <button
                className="primary"
                disabled={busy}
                onClick={() => void act('/parent/connect')}
              >
                {busy ? <LoaderCircle className="spin" size={18} /> : <Plus size={19} />}{' '}
                {me.connector === 'mock' ? 'הפעלת המתאם המדומה' : 'קישור החשבון'}
              </button>
            )}
            {data && <DisconnectPanel active={!!data.linked || data.state !== 'idle'} busy={busy}
              mock={me.connector === 'mock'} result={data.disconnect} history={data.disconnectHistory} onDisconnect={async () => {
                setBusy(true);
                try { await api('/parent/disconnect', 'POST', { erase: false }); setQr(null); await load(); }
                finally { setBusy(false); }
              }} />}
            <div className="small-note">
              <AlertCircle size={14} />
              <p>
                חיבור לא רשמי: הוא עלול להישבר או לגרום להגבלת החשבון. מכשירים מקושרים עשויים להתנתק
                אחרי 14 יום ללא שימוש בטלפון הראשי.
              </p>
            </div>
          </section>
        </details>
        <ChildTabletSetup child={selectedChild} loaded={!!data} stale={overviewStale} busy={busy}
          origin={location.origin} openChat={openChat}
          onPassword={id => { setSettingsChild(id); setSettingsMode('password'); setSettings(true); }}/>
        <DiagnosticsPanel />
        <UpdatesPanel />
        <details className="transparency">
          <summary>מה חשוב לדעת על המרחב הזה?</summary>
          <p>
            זו עדיין זהות ההורה: ההודעות נשלחות מהמספר שלו, והוא יכול לראות אותן ב־WhatsApp. השרת
            הוא מכשיר מקושר שעשוי לקבל ולפענח גם שיחות שאינן מוצגות לילדים. שיחות מוכרות במשפחה
            נשמרות עבור ההורה גם בלי אישור לילד. רק שיחות שאושרו זמינות לילד; אין כאן פרטיות
            מההורה או הצפנה מקצה לקצה עד הדפדפן. מידע ניהולי של שיחות זמין להורה בלבד.
          </p>
          <p>
            ההיסטוריה עלולה להיות חלקית, במיוחד לאחר ניתוק. אין הבטחה לשחזור מלא. שיחות קול ווידאו,
            סקרים ותוכן חד־פעמי אינם נתמכים. גיבויים מוצפנים עשויים להכיל מידע שנמחק עד לסיום השמירה
            המוגדרת.
          </p>
        </details>
        <div className="danger-zone">

          <button
            disabled={busy}
            onClick={() => {
              if (
                confirm(
                  'לנתק ולמחוק את כל השיחות, המדיה והקישור בשרת עבור המשפחה? גיבויים יפוגו לפי מדיניות השמירה.',
                )
              )
                void act('/parent/disconnect', { erase: true });
            }}
          >
            ניתוק ומחיקת נתוני השיחות
          </button>
          <button onClick={() => setDeleteOpen(true)}>מחיקת המשפחה והחשבונות</button>
        </div>
      </div>
      {addingChild && <FamilySetup onDone={id => { setAddingChild(false); setOverviewStale(true); if (id) { setChild(id); setCreatedChild(id); } void load(); }} onClose={() => { setAddingChild(false); setOverviewStale(true); void load(); }} />}
      {settings && (
        <AccountSettings
          children={data?.children}
          initialChildId={settingsChild}
          initialMode={settingsMode}
          onProfileChanged={() => { setOverviewStale(true); void load(); }}
          onClose={() => setSettings(false)}
        />
      )}
      {deleteOpen && (
        <div className="modal-backdrop">
          <form
            className="modal"
            role="dialog"
            aria-modal="true"
            aria-label="מחיקת משפחה"
            onSubmit={async (e) => {
              e.preventDefault();
              setConfirmError('');
              try {
                await api('/parent/family', 'DELETE', { password });
                window.dispatchEvent(new Event('session-expired'));
              } catch (e) {
                setConfirmError((e as Error).message);
              }
            }}
          >
            <h2>מחיקת המשפחה</h2>
            <p>
              החשבונות, ההרשאות, השיחות והמדיה הפעילים יימחקו. אין דרך לבטל. גיבויים קיימים פגים לפי
              מדיניות השמירה; עותקים שהורדו למכשירים נשארים שם.
            </p>
            <label>
              סיסמת ההורה
              <input
                type="password"
                required
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                autoComplete="current-password"
              />
            </label>
            {confirmError && (
              <p role="alert" className="error">
                {confirmError}
              </p>
            )}
            <button className="danger-button">מחיקה סופית</button>
            <button type="button" className="text-button" onClick={() => setDeleteOpen(false)}>
              ביטול
            </button>
          </form>
        </div>
      )}
    </main>
  );
}
function IdentitySettings({
  childId,
  value,
  onSave,
  onError,
  disabled,
}: {
  childId: string;
  disabled: boolean;
  value: string;
  onSave: (label: string) => Promise<void>;
  onError: (x: string) => void;
}) {
  const [label, setLabel] = useState(value),
    [saved, setSaved] = useState(false),
    [busy, setBusy] = useState(false);
  const submitting = useRef(false);
  useEffect(() => { setLabel(value); setSaved(false); }, [value]);
  return (
    <form
      className="parent-card identity-settings"
      onSubmit={async (e) => {
        e.preventDefault();
        if (submitting.current || disabled || !childId) return;
        submitting.current = true; setBusy(true);
        try {
          await onSave(label.trim());
          setSaved(true);
        } catch (e) {
          onError((e as Error).message);
        } finally {
          submitting.current = false; setBusy(false);
        }
      }}
    >
      <div>
        <span className="eyebrow">מי כותב או כותבת?</span>
        <h2>חתימה אוטומטית להודעות</h2>
        <p>השולח ב־WhatsApp נשאר ההורה. חתימה עוזרת למשתתפים לדעת מי כתב או כתבה.</p>
      </div>
      <label>
        כיתוב שההורה בוחר
        <input
          aria-label="חתימה אוטומטית"
          maxLength={40}
          disabled={disabled || busy}
          value={label}
          placeholder="ללא חתימה"
          onChange={(e) => {
            setLabel(e.target.value);
            setSaved(false);
          }}
        />
      </label>
      <div className="signature-preview">
        {label.trim()
          ? `${label.trim()}: היי, מתי נפגשים?`
          : 'ללא חתימה · ההודעות נשלחות מהמספר של ההורה'}
      </div>
      <p>
        בטקסט ובכיתובי תמונות, סרטונים ומסמכים החתימה מצורפת ישירות. לפני הודעה קולית או מדבקה נשלחת
        הודעת זיהוי נפרדת. אם היא נשלחה והמדיה נכשלה, היא עשויה להישאר לבדה. תגובות אימוג׳י תמיד
        מופיעות בשם ההורה. החתימה אינה הוכחת זהות.
      </p>
      <button className="outline-button" disabled={!childId || busy || disabled || label.trim() === value}>
        {busy ? 'שומרים…' : saved && !disabled ? 'החתימה נשמרה' : 'שמירת החתימה'}
        <Check size={17} />
      </button>
    </form>
  );
}
class RenderBoundary extends React.Component<{ children: React.ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() {
    return { failed: true };
  }
  render() {
    return this.state.failed ? (
      <main className="splash">
        <AlertCircle />
        <h1>לא הצלחנו להציג את השיחה</h1>
        <p>אפשר לרענן ולנסות שוב.</p>
        <button className="primary" onClick={() => location.reload()}>
          רענון
        </button>
      </main>
    ) : (
      this.props.children
    );
  }
}
createRoot(document.getElementById('root')!).render(
  <RenderBoundary>
    <App />
  </RenderBoundary>,
);
