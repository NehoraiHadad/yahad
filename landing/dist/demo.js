
(() => {
'use strict';
const root = document.getElementById('family-demo');
if (!root) return;
const control = document.getElementById('motion-toggle');
const announce = document.getElementById('demo-announcement');
const steps = [...root.querySelectorAll('[data-step]')];
const reduced = window.matchMedia('(prefers-reduced-motion: reduce)');
const chip = root.querySelector('.travel-chip');
const chipAvatar = chip && chip.querySelector('i');
const chipLabel = chip && chip.querySelector('span');
const fromEls = [root.querySelector('.pr1 .contact-avatar'), root.querySelector('.pr2 .contact-avatar')];
const toEls = [root.querySelector('.cr1 .contact-avatar'), root.querySelector('.cr2 .contact-avatar')];
const chipTexts = [['מ', 'המשפחה'], ['כ', 'הכיתה של נועה']];
const labels = [
  'ההורה מאשר את קבוצת המשפחה ואת קבוצת הכיתה של נועה.',
  'הקבוצות שאושרו עוברות למסך של נועה.',
  'נועה פותחת את קבוצת המשפחה, כותבת ושולחת, וסבתא עונה בתוך הקבוצה. ההודעות נשלחות מחשבון ההורה עם שמה.'
];
/* beat timeline (ms): [time, class, data-stage] */
const BEATS = [
  [400, 's-tap', 0], [1000, 's-check', 0], [1800, 's-tap2', 0], [2400, 's-check2', 0],
  [3300, 's-travel', 1], [4500, 's-arrive', 1],
  [5200, 's-travel2', 1], [6400, 's-arrive2', 1],
  [7200, 's-open', 2], [8000, 's-write', 2], [8700, 's-msg1', 2],
  [9400, 's-typing', 2], [10200, 's-reply', 2], [10700, 's-compose', 2], [11100, 's-done', 2]
];
const ORDER = BEATS.map(b => b[1]);
const FLIGHTS = [[3300, 4500], [5200, 6400]];  /* chat chips: parent list -> child list */
const END = 11100;                    /* last beat */
const FINAL = END + 400;              /* settled final frame */
const STEP_T = [0, 6800, FINAL];      /* what each step pill shows */

/* button: one table keeps visible text and aria-label in sync */
const BUTTON = {
  playing: ['עצירת ההמחשה', 'Ⅱ'],
  paused: ['המשך ההמחשה', '←'],
  replay: ['ניגון ההמחשה', '↻'],
  next: ['לשלב הבא', '←']
};

let state = 'idle';        /* idle | playing | paused | done */
let pausedAnimations = [];
let userPaused = false;    /* a manual pause survives scrolling away and back */
let inView = false, started = false;
let t0 = null, pausedAt = 0, curT = FINAL, raf = 0, beatTimers = [];
let chipOX = 0, chipOY = 0;
let flights = [];   /* per flight: {t0,t1,from,to,ctrl,sFrom,sTo} */
let chipFlight = -1;

const classesAt = t => BEATS.filter(b => b[0] <= t).map(b => b[1]);
function stageAt(t) { let s = 0; for (const b of BEATS) if (b[0] <= t) s = b[2]; return s; }
const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
/* position: smooth ease-in-out (quint-ish) — slow lift, confident glide, soft landing */
function ease(p) { return p < .5 ? 16 * p ** 5 : 1 - Math.pow(-2 * p + 2, 5) / 2; }
function easeSoft(p) { return p < .5 ? 4 * p ** 3 : 1 - Math.pow(-2 * p + 2, 3) / 2; }

function centerIn(el, box) {
  const r = el.getBoundingClientRect();
  return { x: r.left - box.left + r.width / 2, y: r.top - box.top + r.height / 2, w: el.offsetWidth };
}

/* measure everything that depends on layout; safe to call at any time */
function measure() {
  if (!chip) return;
  /* List and chat share a stable grid cell; hidden targets remain measurable.
     Never toggle event classes while measuring: that would restart animations. */
  /* measure the chip untransformed so its avatar offset is exact */
  const prevT = chip.style.transform;
  chip.style.transform = 'none';
  const cb = chip.getBoundingClientRect();
  const ab = chipAvatar.getBoundingClientRect();
  chipOX = ab.left - cb.left + ab.width / 2;
  chipOY = ab.top - cb.top + ab.height / 2;
  chip.style.transform = prevT;
  chip.style.setProperty('--chip-ox', chipOX + 'px');
  const box = root.getBoundingClientRect();
  flights = FLIGHTS.map((w, i) => {
    const a = centerIn(fromEls[i], box);
    const c = centerIn(toEls[i], box);
    const dy = c.y - a.y;
    return {
      t0: w[0], t1: w[1],
      from: { x: a.x, y: a.y }, to: { x: c.x, y: c.y },
      sFrom: a.w / chipAvatar.offsetWidth, sTo: c.w / chipAvatar.offsetWidth,
      /* gentle outward arc (towards the reading side), never a line on screen */
      ctrl: { x: Math.max(a.x, c.x) + Math.min(10, Math.abs(dy) * .05), y: a.y + dy * .45 }
    };
  });
}


function placeChip(t) {
  if (!flights.length || !chip) return;
  const i = flights.findIndex(f => t >= f.t0 && t < f.t1 + 160);
  if (i === -1) {
    chip.style.opacity = '0'; chip.style.visibility = 'hidden'; chipFlight = -1; return;
  }
  const f = flights[i];
  if (chipFlight !== i) {
    chipFlight = i; chipAvatar.textContent = chipTexts[i][0]; chipLabel.textContent = chipTexts[i][1];
    /* label width shifts the avatar's offset in RTL; remeasure for this chip */
    const prevT = chip.style.transform; chip.style.transform = 'none';
    const cb = chip.getBoundingClientRect(); const ab = chipAvatar.getBoundingClientRect();
    chipOX = ab.left - cb.left + ab.width / 2; chipOY = ab.top - cb.top + ab.height / 2;
    chip.style.transform = prevT;
  }
  const p = clamp((t - f.t0) / (f.t1 - f.t0), 0, 1);
  const e = ease(p), q = 1 - e;
  const x = q * q * f.from.x + 2 * q * e * f.ctrl.x + e * e * f.to.x;
  const y = q * q * f.from.y + 2 * q * e * f.ctrl.y + e * e * f.to.y;
  const lift = Math.sin(Math.PI * easeSoft(p));            /* 0 -> 1 -> 0 */
  const s = (f.sFrom + (f.sTo - f.sFrom) * e) * (1 + .07 * lift);
  const fadeIn = clamp(p / .06, 0, 1);
  const fadeOut = t > f.t1 ? 1 - (t - f.t1) / 160 : 1;
  chip.style.visibility = 'visible';
  chip.style.opacity = String(Math.min(fadeIn, fadeOut));
  chip.style.setProperty('--chip-shadow',
    '0 ' + (6 + 16 * lift).toFixed(1) + 'px ' + (14 + 22 * lift).toFixed(1) + 'px #061e16' + (lift > .5 ? '80' : '66'));
  chip.style.transform = 'translate(' + (x - chipOX).toFixed(2) + 'px,' + (y - chipOY).toFixed(2) + 'px) scale(' + s.toFixed(4) + ')';
}

function render(t) {
  curT = t;
  const want = new Set(classesAt(t));
  for (const c of ORDER) root.classList.toggle(c, want.has(c));
  const st = stageAt(t);
  root.dataset.stage = String(st);
  steps.forEach((b, i) => b.setAttribute('aria-pressed', String(i === st)));
  placeChip(t);
}

/* jump to a state with no transitions */
function snap(t) {
  root.classList.add('is-snap');
  render(t);
  void root.offsetWidth;
  root.classList.remove('is-snap');
}

function setButton(mode) {
  if (!control) return;
  const [label, glyph] = BUTTON[mode];
  control.dataset.mode = mode;
  control.textContent = label;
  const g = document.createElement('span');
  g.setAttribute('aria-hidden', 'true');
  g.textContent = glyph;
  // Keep the motion control text-only; its accessible name matches its label.
  control.setAttribute('aria-label', label);
}

function clearBeats() { for (const id of beatTimers) clearTimeout(id); beatTimers = []; }
function cancelPausedAnimations() {
  pausedAnimations.forEach(a => a.cancel()); pausedAnimations = [];
}
function stopClock() { clearBeats(); cancelAnimationFrame(raf); raf = 0; t0 = null; }

function scheduleBeats(fromT) {
  clearBeats();
  for (const b of BEATS) if (b[0] > fromT) beatTimers.push(setTimeout(() => render(b[0]), b[0] - fromT));
  beatTimers.push(setTimeout(finish, Math.max(0, FINAL - fromT)));
}

function tick(now) {
  if (state !== 'playing') return;
  if (t0 === null) t0 = now - pausedAt;
  const t = Math.min(now - t0, FINAL);
  curT = t;
  placeChip(t);
  raf = requestAnimationFrame(tick);
}

function finish() {
  stopClock();
  render(FINAL);
  state = 'done';
  root.classList.remove('is-paused', 'is-live');
  setButton(reduced.matches ? 'next' : 'replay');
  announce.textContent = labels[2];
}

function play() {
  stopClock(); cancelPausedAnimations();
  measure();
  snap(0);
  state = 'playing'; started = true; userPaused = false; pausedAt = 0;
  root.classList.remove('is-paused');
  root.classList.add('is-live');
  scheduleBeats(0);
  raf = requestAnimationFrame(tick);
  setButton('playing');
  announce.textContent = labels[0];
}

function pause(byUser) {
  if (state !== 'playing') return;
  pausedAt = t0 === null ? curT : Math.min(performance.now() - t0, FINAL);
  curT = pausedAt;
  stopClock();
  state = 'paused';
  if (byUser) userPaused = true;
  pausedAnimations = root.getAnimations ? root.getAnimations({subtree:true}).filter(a => a.playState === 'running') : [];
  pausedAnimations.forEach(a => a.pause());
  root.classList.add('is-paused');
  setButton('paused');
}

function resume() {
  if (state !== 'paused') return;
  userPaused = false;
  state = 'playing';
  root.classList.remove('is-paused');
  pausedAnimations.forEach(a => { if (a.playState === 'paused') a.play(); }); pausedAnimations = [];
  measure(); render(pausedAt);
  scheduleBeats(pausedAt);
  t0 = null;
  raf = requestAnimationFrame(tick);
  setButton('playing');
}

/* show one step, no motion */
function showStep(i, spoken) {
  stopClock(); cancelPausedAnimations();
  root.classList.remove('is-paused', 'is-live');
  measure(); snap(STEP_T[i]);
  state = 'done'; started = true; userPaused = false;
  setButton(reduced.matches ? 'next' : 'replay');
  if (spoken) announce.textContent = labels[i];
}

/* any layout change: remeasure and redraw the current frame (playing, paused or still) */
let remeasureQueued = false;
function remeasure() {
  if (remeasureQueued) return;
  remeasureQueued = true;
  requestAnimationFrame(() => {
    remeasureQueued = false;
    measure();
    placeChip(state === 'paused' ? pausedAt : curT);
  });
}

if (control) {
  control.hidden = false;
  control.addEventListener('click', () => {
    if (reduced.matches) showStep((stageAt(curT) + 1) % 3, true);
    else if (state === 'playing') pause(true);
    else if (state === 'paused') resume();
    else play();
  });
}
steps.forEach((button, i) => button.addEventListener('click', () => showStep(i, true)));
const watch = document.getElementById('watch-demo');
if (watch) watch.addEventListener('click', () => {
  root.scrollIntoView({behavior: reduced.matches ? 'instant' : 'smooth', block:'center'});
  if (reduced.matches) showStep(0, true); else play();
});
const menu = document.querySelector && document.querySelector('.menu-toggle');
const navigation = document.getElementById('navigation');
if (menu && navigation) {
 menu.addEventListener('click', () => { const open=menu.getAttribute('aria-expanded')!=='true'; menu.setAttribute('aria-expanded',String(open)); navigation.classList.toggle('is-open',open); });
 navigation.querySelectorAll('a').forEach(link=>link.addEventListener('click',()=>{ menu.setAttribute('aria-expanded','false'); navigation.classList.remove('is-open'); }));
 document.addEventListener('keydown', event=>{ if(event.key==='Escape'){ menu.setAttribute('aria-expanded','false'); navigation.classList.remove('is-open'); menu.focus(); }});
}

reduced.addEventListener('change', () => {
  /* cancel everything already scheduled, settle on the final frame */
  stopClock(); cancelPausedAnimations(); started = true;
  root.classList.remove('is-paused', 'is-live');
  userPaused = false;
  measure(); snap(FINAL);
  state = 'done';
  setButton(reduced.matches ? 'next' : 'replay');
});

function autoResumeIfAllowed() {
  if (state === 'paused' && !userPaused && inView && !document.hidden && !reduced.matches) resume();
}
document.addEventListener('visibilitychange', () => {
  if (document.hidden) pause(false); else autoResumeIfAllowed();
});
window.addEventListener('resize', remeasure);
if ('ResizeObserver' in window) new ResizeObserver(remeasure).observe(root);
if (document.fonts && document.fonts.ready) document.fonts.ready.then(remeasure);

/* start in the final composed state (same as static markup), autoplay once in view */
measure(); snap(FINAL);
setButton(reduced.matches ? 'next' : 'replay');
if ('IntersectionObserver' in window) {
  new IntersectionObserver((entries) => {
    for (const entry of entries) {
      inView = entry.isIntersecting;
      if (!inView) pause(false);
      else if (!started && !reduced.matches && !document.hidden) play();
      else autoResumeIfAllowed();
    }
  }, { threshold: 0.35 }).observe(root);
}
})();
