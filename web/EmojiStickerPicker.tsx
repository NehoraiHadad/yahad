import React, { useEffect, useMemo, useRef, useState } from 'react';
import { emojiCategories, searchEmoji, emojiVariants } from './emojiCatalog';
import { useDialogFocus } from './useDialogFocus';

export type PickerSticker = { id: string; url: string; label: string };
type Props = {
  onEmoji: (emoji: string) => void;
  onClose: () => void;
  recentEmojis?: readonly string[];
  onUploadSticker?: () => void;
  /** Only media already authorized for the current conversation; never persist URLs. */
  stickers?: readonly PickerSticker[];
  onSticker?: (sticker: PickerSticker) => void;
  favoriteStickerIds?: readonly string[];
  onFavoriteStickerToggle?: (id: string) => void;
};
export function EmojiStickerPicker({ onEmoji, onClose, recentEmojis = [], onUploadSticker, stickers = [], onSticker, favoriteStickerIds = [], onFavoriteStickerToggle }: Props) {
  const [tab, setTab] = useState<'emoji' | 'stickers'>('emoji');
  const [query, setQuery] = useState('');
  const [variantBase, setVariantBase] = useState<string | null>(null);
  const [category, setCategory] = useState('all');
  const [limit, setLimit] = useState(80);
  const [favoritesOnly, setFavoritesOnly] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const variantRef = useRef<HTMLElement>(null);
  const variantOpener = useRef<HTMLButtonElement | null>(null);
  useEffect(() => { if (variantBase) variantRef.current?.querySelector<HTMLButtonElement>('button[data-variant]')?.focus(); }, [variantBase]);
  const closeVariants = () => { setVariantBase(null); variantOpener.current?.focus(); };
  useDialogFocus(ref, true);
  const matches = useMemo(() => searchEmoji(query, category, recentEmojis), [query, category, recentEmojis]);
  const stickerMatches = stickers.filter(s => (!favoritesOnly || favoriteStickerIds.includes(s.id)) && s.label.toLowerCase().includes(query.trim().toLowerCase()));
  const changeCategory = (value: string) => { setCategory(value); setVariantBase(null); setLimit(80); };
  return <div className="emoji-picker-shade" onMouseDown={e => { if (e.target === e.currentTarget) onClose(); }}>
    <div className="emoji-picker" ref={ref} role="dialog" aria-modal="true" aria-label="אימוג׳י ומדבקות" dir="rtl" onKeyDown={e => { if (e.key === 'Escape') { e.stopPropagation(); if (variantBase) closeVariants(); else onClose(); } }}>
      <header><strong>מוסיפים קצת רגש</strong><button type="button" onClick={onClose} aria-label="סגירת בורר אימוג׳י ומדבקות">✕</button></header>
      <div className="emoji-picker-tabs" aria-label="סוג תוכן">
        <button type="button" aria-pressed={tab === 'emoji'} onClick={() => { setTab('emoji'); setQuery(''); }}>😀 אימוג׳י</button>
        {(onUploadSticker || onSticker) && <button type="button" aria-pressed={tab === 'stickers'} onClick={() => { setTab('stickers'); setQuery(''); }}>▧ מדבקות</button>}
      </div>
      <label className="emoji-picker-search">חיפוש {tab === 'emoji' ? 'אימוג׳י בעברית או באנגלית' : 'מדבקות'}
        <input type="search" value={query} onChange={e => { setQuery(e.target.value); setVariantBase(null); setLimit(80); }} placeholder={tab === 'emoji' ? 'לב, חתול, smile…' : 'שם המדבקה…'} />
      </label>
      {tab === 'emoji' ? <>
        <label className="emoji-picker-category">קטגוריה <select value={category} onChange={e => changeCategory(e.target.value)}>
          <option value="all">כל האימוג׳י</option><option value="recent">🕘 בשימוש לאחרונה</option>
          {emojiCategories.map(([id, label, icon]) => <option key={id} value={id}>{icon} {label}</option>)}
        </select></label>
        <small className="emoji-picker-count" role="status">{matches.length ? `${matches.length} אימוג׳י` : category === 'recent' ? 'האימוג׳י שתבחרו יופיעו כאן במהלך השיחה.' : 'לא נמצאו תוצאות. נסו מילה אחרת או קטגוריה אחרת.'}</small>
        {variantBase && <section ref={variantRef} className="emoji-picker-variants" aria-label="בחירת גוון עור"><div>בחירת גוון עור <button type="button" onClick={closeVariants} aria-label="סגירת בחירת גוון">✕</button></div><div className="emoji-picker-grid">{[variantBase, ...emojiVariants[variantBase]].map((emoji, index) => <button type="button" data-variant key={emoji} aria-label={index === 0 ? 'ללא גוון עור מותאם' : `גוון עור ${emoji}`} onClick={() => onEmoji(emoji)}>{emoji}</button>)}</div></section>}
        <div className="emoji-picker-scroll"><div className="emoji-picker-grid">
          {matches.slice(0, limit).map(entry => <button type="button" key={entry.emoji} title={entry.name} aria-label={entry.name + (emojiVariants[entry.emoji] ? ' — בחירת גוון עור' : '')} onClick={event => { if (emojiVariants[entry.emoji]) { variantOpener.current = event.currentTarget; setVariantBase(entry.emoji); } else onEmoji(entry.emoji); }}>{entry.emoji}</button>)}
        </div>{matches.length > limit && <button type="button" className="emoji-picker-more" onClick={() => setLimit(value => value + 80)}>הצגת אימוג׳י נוספים</button>}</div>
        <footer>Unicode 17 · המראה והתמיכה תלויים במכשיר. חיפוש בעברית תומך גם במילות מפתח נפוצות.</footer>
      </> : <>
        <div className="emoji-picker-sticker-tools">
          {onUploadSticker && <button type="button" onClick={onUploadSticker}>＋ מדבקה מקובץ WebP</button>}
          {!!stickers.length && onFavoriteStickerToggle && <button type="button" aria-pressed={favoritesOnly} onClick={() => setFavoritesOnly(value => !value)}>☆ מועדפות בשיחה</button>}
        </div>
        <div className="emoji-picker-scroll"><div className="emoji-picker-stickers">
          {stickerMatches.map(s => <div key={s.id}>
            <button type="button" disabled={!onSticker} aria-label={`בחירת מדבקה: ${s.label}`} onClick={() => onSticker?.(s)}><img src={s.url} alt={s.label} loading="lazy" /></button>
            {onFavoriteStickerToggle && <button type="button" aria-label={`מדבקה מועדפת: ${s.label}`} aria-pressed={favoriteStickerIds.includes(s.id)} onClick={() => onFavoriteStickerToggle?.(s.id)}>{favoriteStickerIds.includes(s.id) ? '★' : '☆'}</button>}
          </div>)}
        </div>{!stickerMatches.length && <p role="status">{stickers.length ? 'לא נמצאו מדבקות בסינון הזה.' : 'אפשר לבחור מדבקת WebP מהמכשיר. אין כאן קטלוג מדבקות של WhatsApp.'}</p>}</div>
        <footer>המדבקות שמוצגות כאן הן רק אלו שזמינות בשיחה המורשית. בחירה אינה שולחת הודעה.</footer>
      </>}
    </div>
  </div>;
}
