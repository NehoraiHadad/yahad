import React, { useEffect, useState } from 'react';
import { Copy } from 'lucide-react';

/** The support reference under an error: the exact code and the server's request
 * reference, which the parent's diagnostics report lists with the same values. */
export function supportReference(code: string, ref?: string) {
  return ref ? `${code} · ${ref}` : code;
}

export async function copyText(text: string) {
  if (navigator.clipboard && window.isSecureContext) {
    await navigator.clipboard.writeText(text);
    return;
  }
  // Plain-HTTP home addresses have no Clipboard API; a selected field still copies.
  const field = document.createElement('textarea');
  field.value = text; field.setAttribute('readonly', ''); field.className = 'error-reference-copy-source';
  document.body.appendChild(field);
  field.select();
  try { if (!document.execCommand('copy')) throw new Error('Copy unavailable'); }
  finally { field.remove(); }
}

export function ErrorReference({ code, refId }: { code?: string; refId?: string }) {
  const [copied, setCopied] = useState<'idle' | 'done' | 'failed'>('idle');
  useEffect(() => setCopied('idle'), [code, refId]);
  if (!code) return null;
  const reference = supportReference(code, refId);
  return <p className="error-reference">
    <span>קוד לתמיכה: <bdi dir="ltr" className="error-reference-code">{reference}</bdi></span>
    <button type="button" className="text-button error-reference-copy" onClick={() => {
      copyText(reference).then(() => setCopied('done'), () => setCopied('failed'));
    }}><Copy size={14} aria-hidden="true" />{copied === 'done' ? 'הועתק' : 'העתקה'}</button>
    {copied === 'failed' && <span role="status">ההעתקה לא זמינה בדפדפן הזה. אפשר לסמן את הקוד ולהעתיק אותו.</span>}
  </p>;
}

/** An inline error next to the form or panel it belongs to, with its support reference. */
export function InlineError({ error, id }: { error: { text: string; code?: string; ref?: string } | null; id?: string }) {
  if (!error) return null;
  return <div className="inline-error" role="alert" id={id}>
    <p className="error">{error.text}</p>
    <ErrorReference code={error.code} refId={error.ref} />
  </div>;
}
