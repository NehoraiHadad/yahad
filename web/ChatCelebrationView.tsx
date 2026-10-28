import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Flower2, Heart, Sun } from 'lucide-react';
import {
  CELEBRATION_PARTICLES,
  celebrationFlair,
  createCelebrationController,
  type CelebrationBurst,
} from './chatCelebration';

/** Reuses the existing effects preference and personal flair; no new persistence. */
export function useChatCelebration(enabled: boolean, flair: string, scope: string | null) {
  const [burst, setBurst] = useState<CelebrationBurst | null>(null);
  const controller = useRef<ReturnType<typeof createCelebrationController> | null>(null);
  useEffect(() => {
    setBurst(null);
    const media = window.matchMedia('(prefers-reduced-motion: reduce)');
    const current = createCelebrationController({
      allowed: () => enabled && !media.matches && document.visibilityState !== 'hidden',
      flair: celebrationFlair(flair),
      onChange: setBurst,
    });
    controller.current = current;
    const stopMotion = () => {
      if (media.matches || document.visibilityState === 'hidden') current.clear();
    };
    media.addEventListener('change', stopMotion);
    document.addEventListener('visibilitychange', stopMotion);
    return () => {
      current.dispose();
      controller.current = null;
      media.removeEventListener('change', stopMotion);
      document.removeEventListener('visibilitychange', stopMotion);
    };
  }, [enabled, flair, scope]);
  const celebrate = useCallback(() => controller.current?.celebrate(), []);
  return { burst: enabled ? burst : null, celebrate };
}

/** Decorative only: never announces a send/delivery result or captures pointer input. */
export function ChatCelebration({ burst }: { burst: CelebrationBurst | null }) {
  if (!burst) return null;
  const Icon = { flower: Flower2, heart: Heart, sun: Sun }[burst.flair];
  return (
    <div key={burst.id} className="chat-celebration" aria-hidden="true">
      {Array.from({ length: CELEBRATION_PARTICLES }, (_, index) => (
        <span className="chat-celebration-particle" key={index}>
          <Icon size={18} strokeWidth={1.8} focusable="false" />
        </span>
      ))}
    </div>
  );
}
