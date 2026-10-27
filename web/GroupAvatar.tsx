import React, { useState } from 'react';

/** Same-origin image only. Decorative: the adjacent group title supplies its name.
 * retryKey changes only on explicit refresh/connection transitions, never a timer.
 */
export function GroupAvatar({ groupId, title, className = 'avatar av-0', retryKey }: {
  groupId: string; title: string; className?: string; retryKey?: string | number;
}) {
  const url = `/api/groups/${encodeURIComponent(groupId)}/photo` +
    (retryKey === undefined ? '' : `?v=${encodeURIComponent(String(retryKey))}`);
  // Remount just the image state on explicit revisions, including A → B → A.
  return <AvatarImage key={url} url={url} title={title} className={className} />;
}
function AvatarImage({ url, title, className }: { url: string; title: string; className: string }) {
  const [failed, setFailed] = useState(false);
  return <span className={className + ' group-avatar'} aria-hidden="true">
    <span>{title.slice(0, 1)}</span>
    {!failed && <img src={url} alt="" loading="lazy" decoding="async"
      referrerPolicy="no-referrer" onError={() => setFailed(true)} />}
  </span>;
}
