import { HistoryPanel } from './HistoryPanel';
import { MessageImage } from './MessageImage';
import { DisconnectPanel } from './DisconnectPanel';
import { GroupPicker } from './GroupPicker';
import { FamilySetup, passphraseHelp } from './FamilySetup';
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
import { DiagnosticsPanel } from './DiagnosticsPanel';
import { UpdatesPanel } from './UpdatesPanel';
import type { PublicMessage } from '../src/service';
import type { Capabilities } from '../src/connectors/types';
type Me = {
  user: {
    id: string;
    familyId: string;
    role: 'parent' | 'child';
    display: string;
    username: string;
  };
  csrf: string;
  connector: 'mock' | 'baileys';
  capabilities: Partial<Capabilities>;
  connection: string;
  idleMs: number | null;
  maxMedia: number;
  senderLabel?: string;
};
type Group = { id: string; title: string; last?: PublicMessage };
const newId = () => {
  const b = crypto.getRandomValues(new Uint8Array(16));
  b[6] = (b[6] & 15) | 64;
  b[8] = (b[8] & 63) | 128;
  const h = Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
};
const emojiChoices = ['💚', '❤️', '👍', '😂', '✨', '🌸', '🎉', '🙏'];
const time = (n: number) =>
  new Intl.DateTimeFormat('he-IL', { hour: '2-digit', minute: '2-digit' }).format(n);
const groupIcons = [Flower2, MessageCircleHeart, Sun];
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
  const [mode, setMode] = useState('chat');
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
      if (m.user.role === 'parent') setMode((prev) => (prev === 'chat' ? 'parent' : prev));
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
    <>
      {error && (
        <div role="alert" className="toast" onClick={() => setError('')}>
          {error}
        </div>
      )}
      {mode === 'parent' && me.user.role === 'parent' ? (
        <Parent me={me} logout={logout} openChat={() => setMode('chat')} onError={setError} />
      ) : (
        <Chat
          me={me}
          onMe={setMe}
          logout={logout}
          parent={() => setMode('parent')}
          onError={setError}
        />
      )}
    </>
  );
}
function Login({ onLogin }: { onLogin: () => Promise<void> }) {
  const [setupDone, setSetupDone] = useState(false);
  const [username, setUsername] = useState(''),
    [password, setPassword] = useState(''),
    [show, setShow] = useState(false),
    [busy, setBusy] = useState(false),
    [error, setError] = useState('');
  if (setupToken && !setupDone) return <FamilySetup setupToken={setupToken} onDone={() => { setupToken = ''; setSetupDone(true); }} />;
  return (
    <main className="login-page">
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
          איזה כיף שאת כאן <Flower2 />
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
            await api('/login', 'POST', { username, password });
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
          שם משתמש
          <input
            dir="ltr"
            autoComplete="username"
            required
            maxLength={80}
            value={username}
            onChange={(e) => setUsername(e.target.value)}
          />
        </label>
        <label>
          סיסמה
          <div className="password-field">
            <input
              type={show ? 'text' : 'password'}
              autoComplete="current-password"
              dir="ltr"
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
  onError,
}: {
  me: Me;
  onMe: (x: Me) => void;
  logout: () => Promise<void>;
  parent: () => void;
  onError: (x: string) => void;
}) {
  const [groups, setGroups] = useState<Group[]>([]),
    [selected, setSelected] = useState<string | null>(null),
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
  const viewport = useRef<HTMLDivElement>(null),
    bottom = useRef<HTMLDivElement>(null),
    selectedRef = useRef(selected),
    nearBottom = useRef(true),
    requestGeneration = useRef(0),
    typingTimer = useRef<ReturnType<typeof setTimeout> | null>(null),
    seenIds = useRef(new Set<string>());
  selectedRef.current = selected;
  const current = groups.find((g) => g.id === selected),
    c = me.capabilities;
  const refreshList = useCallback(async () => {
    try {
      const data = await api<Group[]>('/groups');
      setGroups(data);
      setNeedsRefresh(false);
      setListBusy(false);
      const g = selectedRef.current;
      if (g && !data.some((x) => x.id === g)) {
        requestGeneration.current++;
        setSelected(null);
        setMessages([]);
        setReply(null);
        setResults(null);
        setEditing(null);
        setMenu(null);
      }
    } catch (e) {
      setListBusy(false);
      setNeedsRefresh(true);
    }
  }, []);
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
        const ids = data.messages.filter((m) => !m.fromMe && !m.deleted).map((m) => m.id);
        if (c.read && document.visibilityState === 'visible' && nearBottom.current && ids.length)
          void api('/groups/' + encodeURIComponent(g) + '/read', 'POST', { ids }).catch(() => {});
      } catch (e) {
        if (selectedRef.current === g) {
          setBusy(false);
          onError((e as Error).message);
        }
      }
    },
    [c.read, onError],
  );
  useEffect(() => {
    void refreshList();
    const source = new EventSource('/api/events');
    source.addEventListener('ready', () => {
      setOnline(true);
      void refreshList();
      const g = selectedRef.current;
      if (g) void refreshMessages(g);
    });
    source.addEventListener('refresh', () => {
      void refreshList();
      void api<Me>('/me')
        .then((m) => {
          setCsrf(m.csrf);
          onMe(m);
        })
        .catch(() => {});
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
    source.onerror = () => setOnline(false);
    return () => {
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
    if (selected) {
      setBusy(true);
      void refreshMessages(selected, true);
    }
  }, [selected]);
  useEffect(() => {
    localStorage.setItem('yachad-theme', theme);
    localStorage.setItem('yachad-dark', String(dark));
    localStorage.setItem('yachad-wallpaper', wallpaper);
  }, [theme, dark, wallpaper]);
  const action = async (fn: () => Promise<unknown>) => {
    try {
      await fn();
      if (selected) await refreshMessages(selected);
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
    <main className={`chat-app theme-${theme} ${dark ? 'dark' : ''} ${selected ? 'has-chat' : ''}`}>
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
        <div className="side-title">
          <div>
            <span className="eyebrow">קצת מילים, הרבה ביחד</span>
            <h1>
              השיחות שלי<span className="tiny-flower">✳</span>
            </h1>
          </div>
          <span className="count">{groups.length}</span>
        </div>
        <label className="group-search">
          <Search size={18} />
          <input
            aria-label="חיפוש קבוצה"
            placeholder="איזו שיחה מחפשים?"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
        </label>
        <div className="group-list">
          {listBusy ? (
            <div className="list-loading">
              <LoaderCircle className="spin" />
              טוענים שיחות…
            </div>
          ) : (
            groups
              .filter((g) => g.title.includes(query))
              .map((g, i) => {
                const Icon = groupIcons[i % 3];
                return (
                  <button
                    key={g.id}
                    className={'group-row ' + (g.id === selected ? 'active' : '')}
                    onClick={() => setSelected(g.id)}
                  >
                    <div className={'avatar av-' + (i % 3)}>
                      <Icon size={26} />
                    </div>
                    <div className="group-info">
                      <div className="group-heading">
                        <b>{g.title}</b>
                        <span>{g.last ? time(g.last.time) : ''}</span>
                      </div>
                      <div className="group-preview">
                        {g.last?.deleted
                          ? 'הודעה נמחקה'
                          : g.last?.text ||
                            (g.last?.kind === 'audio'
                              ? '🎙 הודעה קולית'
                              : g.last?.kind === 'image'
                                ? 'תמונה'
                                : 'כאן מתחילה השיחה')}
                      </div>
                    </div>
                    {g.id === selected && <span className="selected-dot" />}
                  </button>
                );
              })
          )}
          {!listBusy && !groups.length && (
            <div className="empty-list">
              <ShieldCheck />
              <h3>עוד רגע, מתחילים</h3>
              <p>הקבוצות שההורה יבחר יופיעו כאן.</p>
              <button className="text-button" onClick={() => void refreshList()}>
                רענון
              </button>
            </div>
          )}
        </div>
        <div className="side-footer">
          <div className="safe-note">
            <ShieldCheck size={16} />
            <span>רק הקבוצות שנבחרו בשבילך</span>
          </div>
          <div className="profile-row">
            <div className="profile-avatar">✦</div>
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
        {current ? (
          <>
            <header className="chat-header">
              <IconButton className="back" label="חזרה לשיחות" onClick={() => setSelected(null)}>
                <ArrowRight />
              </IconButton>
              <div className="avatar av-0">
                <Flower2 size={25} />
              </div>
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
            <Composer
              key={current.id}
              group={current.id}
              caps={c}
              maxMedia={me.maxMedia}
              enabled={online && me.connection === 'connected'}
              reply={reply}
              clearReply={() => setReply(null)}
              editing={editing}
              clearEditing={() => setEditing(null)}
              onSent={() => {
                setReply(null);
                setEditing(null);
                nearBottom.current = true;
                void refreshList();
                void refreshMessages(current.id).then(() => {
                  bottom.current?.scrollIntoView({ behavior: 'smooth' });
                  nearBottom.current = true;
                  setNewCount(0);
                });
              }}
              onError={onError}
            />
          </>
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
              בחרי שיחה, שלחי חיוך,
              <br />
              ושתפי את מה שעובר עלייך.
            </p>
            {needsRefresh ? (
              <button className="small-button" onClick={() => void refreshList()}>
                לא הצלחנו לטעון. לנסות שוב
              </button>
            ) : (
              <span className="welcome-foot">
                <ShieldCheck size={16} /> השיחות שנבחרו מופיעות בצד
              </span>
            )}
            {me.connector === 'mock' && (
              <div className="demo-label">מצב דוגמה · ללא חיבור לחשבון WhatsApp</div>
            )}
          </div>
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
                  onClick={() => setWallpaper(id)}
                >
                  {title}
                </button>
              ))}
            </div>
            <button className="dark-toggle" onClick={() => setDark(!dark)}>
              {dark ? <Moon size={20} /> : <Sun size={20} />} מצב כהה{' '}
              <span className={'switch ' + (dark ? 'on' : '')} />
            </button>
            <button className="primary" onClick={() => setThemeOpen(false)}>
              בדיוק ככה <Check size={18} />
            </button>
          </section>
        </div>
      )}
      {settingsOpen && <AccountSettings onClose={() => setSettingsOpen(false)} onError={onError} />}
    </main>
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
    read: 'החיבור דיווח על קריאה — לא בהכרח כל חברי הקבוצה',
    failed: 'לא התקבל אישור — ייתכן שההודעה הגיעה',
    unknown: 'מצב מסירה אינו ידוע',
  };
  return (
    <article className={'message-row ' + (m.fromMe ? 'outgoing' : 'incoming')}>
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
  caps,
  maxMedia,
  enabled,
  reply,
  clearReply,
  editing,
  clearEditing,
  onSent,
  onError,
}: {
  group: string;
  caps: Partial<Capabilities>;
  maxMedia: number;
  enabled: boolean;
  reply: PublicMessage | null;
  clearReply: () => void;
  editing: PublicMessage | null;
  clearEditing: () => void;
  onSent: () => void;
  onError: (x: string) => void;
}) {
  const [text, setText] = useState(''),
    [busy, setBusy] = useState(false),
    [emojiOpen, setEmojiOpen] = useState(false),
    [attachOpen, setAttachOpen] = useState(false),
    [file, setFile] = useState<File | null>(null),
    [sticker, setSticker] = useState(false),
    [recording, setRecording] = useState(false),
    [voice, setVoice] = useState<Blob | null>(null),
    [seconds, setSeconds] = useState(0),
    [preview, setPreview] = useState('');
  const input = useRef<HTMLTextAreaElement>(null),
    fileInput = useRef<HTMLInputElement>(null),
    recorder = useRef<MediaRecorder | null>(null),
    mediaStream = useRef<MediaStream | null>(null),
    chunks = useRef<Blob[]>([]),
    id = useRef(newId()),
    typingAt = useRef(0),
    mounted = useRef(true),
    cancelRecord = useRef(false);
  const path = '/groups/' + encodeURIComponent(group);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      cancelRecord.current = true;
      recorder.current?.state === 'recording' && recorder.current.stop();
      mediaStream.current?.getTracks().forEach((t) => t.stop());
    };
  }, []);
  useEffect(() => {
    if (editing) {
      setText(
        editing.authorLabel && editing.text.startsWith(editing.authorLabel + ': ')
          ? editing.text.slice(editing.authorLabel.length + 2)
          : editing.text,
      );
      setFile(null);
      setVoice(null);
      input.current?.focus();
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
  const send = async (e?: React.FormEvent) => {
    e?.preventDefault();
    if (busy || !enabled || recording || (!text.trim() && !file && !voice)) return;
    setBusy(true);
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
      onSent();
    } catch (e) {
      if (mounted.current) onError((e as Error).message);
    } finally {
      if (mounted.current) setBusy(false);
    }
  };
  const stopRecording = () => {
    recorder.current?.state === 'recording' && recorder.current.stop();
    mediaStream.current?.getTracks().forEach((t) => t.stop());
    setRecording(false);
  };
  const record = async () => {
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
      if (!mounted.current) {
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
    <div className="composer-wrap">
      {reply && !editing && (
        <div className="reply-preview">
          <Reply size={18} />
          <div>
            <b>בתשובה ל־{reply.sender}</b>
            <span>{reply.text || 'קובץ מצורף'}</span>
          </div>
          <IconButton label="ביטול מענה" onClick={clearReply}>
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
          <Paperclip size={18} />
          {file.name}
          <span>{(file.size / 1024 ** 2).toFixed(1)} MB</span>
          <IconButton label="הסרת קובץ" onClick={() => setFile(null)}>
            <X size={17} />
          </IconButton>
        </div>
      )}
      {voice && (
        <div className="attachment-preview">
          <Mic size={18} />
          <audio controls src={preview} />
          <IconButton label="מחיקת הקלטה" onClick={() => setVoice(null)}>
            <Trash2 size={18} />
          </IconButton>
        </div>
      )}
      {emojiOpen && (
        <div className="composer-popover emojis">
          {[
            ...emojiChoices,
            '😊',
            '🌿',
            '🎨',
            '☀️',
            '🦋',
            '🍓',
            '💫',
            '🤗',
            '🤍',
            '🌈',
            '👌',
            '🥳',
          ].map((e) => (
            <button
              key={e}
              aria-label={'אימוג׳י ' + e}
              onClick={() => {
                setText((t) => t + e);
                setEmojiOpen(false);
                input.current?.focus();
              }}
            >
              {e}
            </button>
          ))}
        </div>
      )}
      {attachOpen && (
        <div className="composer-popover attach-menu">
          {(caps.image || caps.video || caps.document) && (
            <button
              onClick={() => {
                setSticker(false);
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
                setSticker(true);
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
              setVoice(null);
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
  const [identity, setIdentity] = useState('');
  const [overviewStale, setOverviewStale] = useState(false);
  const [data, setData] = useState<any>(null),
    [qr, setQr] = useState<string | null>(null),
    [busy, setBusy] = useState(false),
    [child, setChild] = useState(''),
    [settings, setSettings] = useState(false),
    [addingChild, setAddingChild] = useState(false),
    [deleteOpen, setDeleteOpen] = useState(false),
    [password, setPassword] = useState(''),
    [confirmError, setConfirmError] = useState('');
  const load = useCallback(async (background = false) => {
    try {
      const d = await api('/parent/overview');
      setData(d);
      setChild((prev) => prev || d.children[0]?.id || '');
      if (d.state === 'qr') {
        const q = await api('/parent/qr');
        setQr(q.qr);
      } else setQr(null);
      setOverviewStale(false);
    } catch (e) {
      if (background && isTransientApiError(e)) setOverviewStale(true);
      else onError((e as Error).message);
    }
  }, [onError]);
  useEffect(() => {
    void load();
    const source = new EventSource('/api/events');
    source.addEventListener('refresh', () => void load(true));
    source.addEventListener('expired', () => window.dispatchEvent(new Event('session-expired')));
    const timer = setInterval(() => void load(true), 10000);
    return () => {
      source.close();
      clearInterval(timer);
    };
  }, []);
  const act = async (path: string, body?: unknown, method = 'POST') => {
    setBusy(true);
    try {
      await api(path, method, body);
      await load();
    } catch (e) {
      onError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };
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
          <button className="text-button" onClick={() => setSettings(true)}>
            <Settings size={17} />
            הגדרות חשבון
          </button>
          <IconButton label="יציאה" onClick={() => void logout()}>
            <LogOut size={20} />
          </IconButton>
        </div>
      </header>
      <div className="parent-content">
        <span className="eyebrow">בוחרים את המרחב, משאירים מקום לשיחה</span>
        <h1>
          הקבוצות שלה.
          <br />
          <span>הבחירה שלכם.</span>
        </h1>
        <p className="parent-intro">מקשרים חשבון, בוחרים קבוצות, ונכנסים לשיחות בטאבלט.</p>
        {overviewStale && <p className="diagnostics-caption" role="status">עדכון המסך ממתין לחיבור לשרת. ננסה שוב אוטומטית; המידע שכבר מוצג עשוי להיות לא מעודכן. זו אינה הודעת כשל של פעולה שביקשתם.</p>}
        {me.connector === 'mock' && (
          <div className="mock-notice">
            <Flower2 size={21} />
            <div>
              <b>אתם בסביבת דוגמה בטוחה</b>
              <p>אין חיבור ל־WhatsApp. כל השיחות והמדיה כאן הן דוגמאות מקומיות.</p>
            </div>
          </div>
        )}
        <div className="parent-grid">
          <section className="parent-card">
            <div className="step-title">
              <span>01</span>
              <h2>מחברים את WhatsApp</h2>
              <span className={'state-pill ' + (data?.state === 'connected' ? 'connected' : '')}>
                {(data?.disconnect ? 'מנותק' : stateLabels[data?.state]) || 'טוענים…'}
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
          <section className="parent-card group-picker">
            <div className="step-title">
              <span>02</span>
              <h2>בוחרים את הקבוצות</h2>
              <IconButton
                label="רענון רשימת קבוצות"
                disabled={busy || data?.state !== 'connected'}
                onClick={() => void act('/parent/sync-groups')}
              >
                <RefreshCw size={17} />
              </IconButton>
            </div>
            <p>רק הקבוצות המסומנות נגישות בחשבון הילדה.</p>
            <button type="button" className="outline-button" onClick={() => setAddingChild(true)}>הוספת ילד או ילדה</button>
            {data?.children.length > 1 && (
              <label>
                חשבון ילדה
                <select value={child} disabled={busy} onChange={(e) => setChild(e.target.value)}>
                  {data.children.map((x: any) => (
                    <option key={x.id} value={x.id}>
                      {x.username}
                    </option>
                  ))}
                </select>
              </label>
            )}
            <GroupPicker groups={data?.groups || []} grants={data?.grants || []} childId={child}
              busy={busy} loading={!data} onToggle={(groupId, allowed) => void act('/parent/grant', { childId: child, groupId, allowed }, 'PUT')} />
            <div className="small-note">
              <ShieldCheck size={16} />
              <p>
                הסרת סימון מבטלת גישה מיד. כשאין עוד חשבון ילד מורשה בקבוצה, נמחקים העותקים הפעילים
                שבשליטת האפליקציה. קבצים שכבר הורדו למכשיר אינם ניתנים למחיקה מרחוק.
              </p>
            </div>
          </section>
        </div>
        <section className="parent-card next-step">
          <div>
            <span className="eyebrow">03 · מתחילים לדבר</span>
            <h2>מוכנים לטאבלט</h2>
            <p>פותחים את אותה כתובת מאובטחת, ונכנסים עם חשבון הילדה.</p>
          </div>
          <div className="child-login-details">
            שם משתמש{' '}
            <b dir="ltr">{data?.children.find((x: any) => x.id === child)?.username || '—'}</b>
          </div>
          <button className="outline-button" onClick={openChat}>
            לממשק השיחות <ArrowLeft size={18} />
          </button>
        </section>
        <IdentitySettings
          key={child}
          childId={child}
          value={data?.children.find((x: any) => x.id === child)?.senderLabel || ''}
          onSave={async (label) => {
            await api('/parent/identity', 'PUT', { childId: child, label });
            await load();
          }}
          onError={onError}
        />
        <DiagnosticsPanel />
        <UpdatesPanel />
        <details className="transparency">
          <summary>מה חשוב לדעת על המרחב הזה?</summary>
          <p>
            זו עדיין זהות ההורה: ההודעות נשלחות מהמספר שלו, והוא יכול לראות אותן ב־WhatsApp. השרת
            הוא מכשיר מקושר שעשוי לקבל ולפענח גם שיחות שאינן מוצגות לילדה. האפליקציה אינה שומרת תוכן
            שלהן, אך אין כאן פרטיות מההורה או הצפנה מקצה לקצה עד הדפדפן. מידע ניהולי מינימלי של
            קבוצות זמין להורה בלבד.
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
      {addingChild && <FamilySetup onDone={() => { setAddingChild(false); void load(); }} onClose={() => setAddingChild(false)} />}
      {settings && (
        <AccountSettings
          children={data?.children}
          onClose={() => setSettings(false)}
          onError={onError}
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
}: {
  childId: string;
  value: string;
  onSave: (label: string) => Promise<void>;
  onError: (x: string) => void;
}) {
  const [label, setLabel] = useState(value),
    [saved, setSaved] = useState(false),
    [busy, setBusy] = useState(false);
  useEffect(() => setLabel(value), [value]);
  return (
    <form
      className="parent-card identity-settings"
      onSubmit={async (e) => {
        e.preventDefault();
        setBusy(true);
        try {
          await onSave(label.trim());
          setSaved(true);
        } catch (e) {
          onError((e as Error).message);
        } finally {
          setBusy(false);
        }
      }}
    >
      <div>
        <span className="eyebrow">מי כותבת?</span>
        <h2>חתימה אוטומטית להודעות</h2>
        <p>השולח ב־WhatsApp נשאר ההורה. חתימה עוזרת למשתתפים לדעת מי כתבה.</p>
      </div>
      <label>
        כיתוב שההורה בוחר
        <input
          aria-label="חתימה אוטומטית"
          maxLength={40}
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
      <button className="outline-button" disabled={!childId || busy}>
        {saved ? 'החתימה נשמרה' : 'שמירת החתימה'}
        <Check size={17} />
      </button>
    </form>
  );
}
function AccountSettings({
  onClose,
  onError,
  children: users,
}: {
  onClose: () => void;
  onError: (x: string) => void;
  children?: { id: string; username: string }[];
}) {
  const [current, setCurrent] = useState(''),
    [next, setNext] = useState(''),
    [confirmation, setConfirmation] = useState(''),
    [show, setShow] = useState(false),
    [child, setChild] = useState(''),
    [busy, setBusy] = useState(false),
    [message, setMessage] = useState('');
  return (
    <div className="modal-backdrop">
      <form
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-label="הגדרות חשבון"
        onSubmit={async (e) => {
          e.preventDefault();
          if (next !== confirmation) { onError('הסיסמאות אינן זהות.'); return; }
          setBusy(true);
          try {
            if (child) {
              await api('/parent/child-password', 'POST', { childId: child, password: next, confirmation });
              setMessage('הסיסמה עודכנה וכל חיבורי החשבון נותקו.');
              setNext(''); setConfirmation('');
            } else {
              await api('/password', 'POST', { current, next, confirmation });
              window.dispatchEvent(new Event('session-expired'));
            }
          } catch (e) {
            onError((e as Error).message);
          } finally {
            setBusy(false);
          }
        }}
      >
        <div className="modal-top">
          <h2>הגדרות חשבון</h2>
          <IconButton label="סגירה" onClick={onClose}>
            <X />
          </IconButton>
        </div>
        {users && (
          <label>
            איזה חשבון מעדכנים?
            <select value={child} onChange={(e) => { setChild(e.target.value); setNext(''); setConfirmation(''); setMessage(''); }}>
              <option value="">החשבון שלי</option>
              {users.map((u) => (
                <option key={u.id} value={u.id}>
                  {u.username}
                </option>
              ))}
            </select>
          </label>
        )}
        {!child && (
          <label>
            סיסמה נוכחית
            <input
              type="password"
              autoComplete="current-password"
              required
              value={current}
              onChange={(e) => setCurrent(e.target.value)}
            />
          </label>
        )}
        <label>
          סיסמה חדשה · לפחות 14 תווים
          <input
            type={show ? "text" : "password"}
            autoComplete="new-password"
            required
            minLength={14}
            maxLength={256}
            value={next}
            onChange={(e) => setNext(e.target.value)}
          />
        </label>
        <label>אישור הסיסמה החדשה<input type={show ? 'text' : 'password'} autoComplete="new-password" required minLength={14} maxLength={256} value={confirmation} onChange={e => setConfirmation(e.target.value)} /></label>
        <label className="secret-toggle"><input type="checkbox" checked={show} onChange={e => setShow(e.target.checked)} /> הצגת הסיסמה</label>
        <p className="muted">{passphraseHelp}</p>
        <p className="muted">
          שינוי סיסמה מנתק את כל החיבורים של החשבון. אפשר להשתמש במשפט סודי שקל לזכור.
        </p>
        {message && <p role="status">{message}</p>}
        <button className="primary" disabled={busy}>
          עדכון סיסמה <Check size={17} />
        </button>
      </form>
    </div>
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
