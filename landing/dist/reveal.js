/* Optional motion only. The static/no-JS experience is complete and visible.
 * Observer failure, reduced motion, keyboard focus and fragment navigation
 * must never leave content hidden. No network, storage or dependencies. */
(() => {
'use strict';
if (!('IntersectionObserver' in window)) return;
const media = window.matchMedia('(prefers-reduced-motion: reduce)');
if (media.matches) return;
const els = [...document.querySelectorAll('.boundaries, .disclaimer, .faq, .connection-disclosure, footer')];
if (!els.length) return;
const root = document.documentElement;
let io;
function reveal(el) {
  el.classList.add('is-visible');
  if (io) io.unobserve(el);
}
function revealContaining(target) {
  if (!target) return;
  for (const el of els) if (el === target || el.contains(target)) reveal(el);
}
function revealFragment() {
  try { revealContaining(document.getElementById(decodeURIComponent(window.location.hash.slice(1)))); }
  catch (_) { /* malformed fragment: ignore without hiding baseline content */ }
}
function failOpen() {
  root.classList.remove('js-reveal');
  els.forEach(el => el.classList.add('is-visible'));
  if (io) io.disconnect();
}
try {
  io = new IntersectionObserver(entries => {
    try { for (const entry of entries) if (entry.isIntersecting) reveal(entry.target); }
    catch (_) { failOpen(); }
  }, { threshold: 0.15 });
  // Register all observers before opting into hidden entrance states.
  els.forEach(el => io.observe(el));
  els.forEach(el => el.classList.add('reveal'));
  root.classList.add('js-reveal');
  document.addEventListener('focusin', event => revealContaining(event.target));
  window.addEventListener('hashchange', revealFragment);
  if (media.addEventListener) media.addEventListener('change', failOpen);
  else if (media.addListener) media.addListener(failOpen);
  revealContaining(document.activeElement);
  revealFragment();
} catch (_) { failOpen(); }
})();
