import React, { useEffect, useState } from 'react';
import { explainConnection, type ConnectionInput } from './connectionGuidance';

export function useBrowserOnline() {
  const [online, setOnline] = useState(() => typeof navigator === 'undefined' || navigator.onLine);
  useEffect(() => {
    const on = () => setOnline(true), off = () => setOnline(false);
    window.addEventListener('online', on); window.addEventListener('offline', off);
    return () => { window.removeEventListener('online', on); window.removeEventListener('offline', off); };
  }, []);
  return online;
}

/** Explains the current connection state to the parent. Renders nothing while healthy or still loading. */
export function ConnectionGuidance({ input, loaded }: { input: ConnectionInput; loaded: boolean }) {
  if (!loaded && !input.overviewFailed && input.browserOnline) return null;
  const g = explainConnection(input);
  if (g.code === 'ok') return null;
  return <div className={'connection-guidance ' + g.tone} role="status" data-connection-code={g.code}>
    <b>{g.title}</b>
    <p>{g.detail}</p>
    {g.steps.length > 0 && <ol>{g.steps.map(s => <li key={s}>{s}</li>)}</ol>}
    <small>{g.recoversAlone ? 'אין צורך ללחוץ על כלום. המסך מתעדכן לבד.' : g.needsQr ? 'נדרשת פעולה שלכם עם קוד QR.' : 'נדרשת בדיקה שלכם. לא מתבצעת שום פעולה אוטומטית.'}</small>
  </div>;
}
