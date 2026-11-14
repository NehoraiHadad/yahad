/* יחד · how it works.
   The step that crosses the reading line becomes data-step on the section; how.css
   draws every state from that number. This script only (1) reports the active step
   and (2) aims the stage camera at the world rectangle that step is about.
   Without IntersectionObserver the section stays a plain numbered list. */
(() => {
'use strict';
const section = document.getElementById('how-it-works');
if (!section || !('IntersectionObserver' in window)) return;
const stage = section.querySelector('.how-stage');
const cam = section.querySelector('.how-cam');
const steps = [...section.querySelectorAll('.how-step')];
if (!stage || !cam || !steps.length) return;

// World rectangles [x, y, width, height]; must match the coordinates in how.css.
// PH phone, TB tablet, LINK computer + phone (scanning the QR),
// OV the whole path phone -> computer -> tablet.
const WORLD = {
  wide: { OV: [0, 0, 1180, 680], PH: [880, 50, 280, 580], TB: [20, 144, 560, 392], LINK: [620, 50, 540, 580] },
  tall: { OV: [0, 0, 600, 1300], PH: [160, 0, 280, 580], TB: [20, 900, 560, 392], LINK: [160, 0, 280, 850] },
};
const TARGET = { 1: 'LINK', 2: 'PH', 3: 'TB', 4: 'PH', 5: 'OV', 6: 'OV', 7: 'OV' };
const MAX_ZOOM = 1.15;
// Reading line as fractions of the viewport height: [from top, from bottom].
// Wide: the middle of the screen. Narrow: just below the sticky stage (56svh).
const LINE = { wide: [0.5, 0.49], narrow: [0.74, 0.25] };
const narrow = window.matchMedia('(max-width: 899px)');
let current = 0;
let observer = null;
let lineHeight = 0;

const fit = (w, h, [, , rw, rh]) => Math.min((w * 0.9) / rw, (h * 0.9) / rh, MAX_ZOOM);

// The world whose overview shows largest in this stage. how.css positions the
// devices from data-world, so this is the only place the choice is made.
function pickWorld(w, h) {
  const world = fit(w, h, WORLD.tall.OV) > fit(w, h, WORLD.wide.OV) ? 'tall' : 'wide';
  if (section.dataset.world !== world) section.dataset.world = world;
  return world;
}

function aim() {
  const w = stage.clientWidth, h = stage.clientHeight;
  if (!w || !h) return;
  const [x, y, rw, rh] = WORLD[pickWorld(w, h)][TARGET[current] || 'OV'];
  const s = fit(w, h, [x, y, rw, rh]);
  const tx = w / 2 - (x + rw / 2) * s, ty = h / 2 - (y + rh / 2) * s;
  cam.style.transform = `translate(${tx.toFixed(1)}px, ${ty.toFixed(1)}px) scale(${s.toFixed(4)})`;
}

function show(step) {
  if (step === current || !TARGET[step]) return;
  current = step;
  section.dataset.step = String(step);
  for (const el of steps) el.classList.toggle('is-active', Number(el.dataset.step) === step);
  aim();
}

// rootMargin percentages resolve against the root's WIDTH on every side (W3C
// IntersectionObserver, "root intersection rectangle"), so the line is set in px
// from the viewport height and rebuilt when that height changes.
function observe() {
  const vh = window.innerHeight;
  if (!vh || (observer && vh === lineHeight)) return;
  lineHeight = vh;
  if (observer) observer.disconnect();
  const [top, bottom] = LINE[narrow.matches ? 'narrow' : 'wide'];
  const rootMargin = `${-Math.round(vh * top)}px 0px ${-Math.round(vh * bottom)}px 0px`;
  observer = new IntersectionObserver(entries => {
    for (const entry of entries) if (entry.isIntersecting) show(Number(entry.target.dataset.step));
  }, { rootMargin });
  for (const el of steps) observer.observe(el);
}

section.classList.add('how-live');
// First placement is instant; only step changes animate the camera.
cam.style.transition = 'none';
show(1);
cam.getBoundingClientRect();
cam.style.transition = '';
observe();
if ('ResizeObserver' in window) new ResizeObserver(aim).observe(stage);
window.addEventListener('resize', () => { observe(); aim(); });
const relayout = () => { lineHeight = 0; observe(); aim(); };
if (narrow.addEventListener) narrow.addEventListener('change', relayout);
else if (narrow.addListener) narrow.addListener(relayout);
})();
