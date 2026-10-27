import React, { Component, Suspense, lazy, useEffect, useMemo, useRef, useState } from 'react';
import type { EmojiStickerPicker } from './EmojiStickerPicker';
import { useDialogFocus } from './useDialogFocus';
type Props = React.ComponentProps<typeof EmojiStickerPicker>;
function PickerStatus({ error, onClose, onRetry }: { error?: boolean; onClose: () => void; onRetry?: () => void }) {
  const ref = useRef<HTMLDivElement>(null);
  useDialogFocus(ref, true);
  return <div className="emoji-picker-shade"><div className="emoji-picker" ref={ref} role="dialog" aria-modal="true" aria-label="אימוג׳י ומדבקות" dir="rtl" onKeyDown={event => { if (event.key === 'Escape') { event.stopPropagation(); onClose(); } }}>
    <p role="status">{error ? 'בורר האימוג׳י לא נטען. אפשר לנסות שוב.' : 'טוענים את האימוג׳י והמדבקות…'}</p>
    {error && <button type="button" onClick={onRetry}>ניסיון נוסף</button>}
    <button type="button" onClick={onClose}>סגירה</button>
  </div></div>;
}
class PickerBoundary extends Component<{ children: React.ReactNode; onClose: () => void; onRetry: () => void }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  render() { return this.state.failed ? <PickerStatus error onClose={this.props.onClose} onRetry={this.props.onRetry} /> : this.props.children; }
}
export function LazyEmojiStickerPicker(props: Props) {
  const opener = useRef<HTMLElement | null>(typeof document === 'undefined' ? null : document.activeElement as HTMLElement | null);
  useEffect(() => () => { if (opener.current?.isConnected) opener.current.focus(); }, []);
  const [attempt, setAttempt] = useState(0);
  const Picker = useMemo(() => lazy(() => import('./EmojiStickerPicker').then(module => ({ default: module.EmojiStickerPicker }))), [attempt]);
  return <PickerBoundary key={attempt} onClose={props.onClose} onRetry={() => setAttempt(value => value + 1)}>
    <Suspense fallback={<PickerStatus onClose={props.onClose} />}><Picker {...props} /></Suspense>
  </PickerBoundary>;
}
