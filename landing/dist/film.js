/* יחד · how it works: plays the film (film-scene.js) next to the four beats.
   The beats are the content and read fine on their own; the film is decoration
   (aria-hidden). It plays once when half of it is on screen, pauses when it leaves,
   and the button pauses, resumes or replays it. A beat's heading becomes a button
   that jumps the film to that beat. With reduced motion nothing plays by itself:
   the film shows one still per beat, and the button still plays it on request.
   Without JS, IntersectionObserver or the scene, the section stays a plain list. */
(() => {
'use strict';
const section = document.getElementById('how-it-works');
const film = window.YahadFilm;
if (!section || !film || !('IntersectionObserver' in window)) return;
const box = section.querySelector('.film');
const frame = section.querySelector('.film-frame');
const toggle = section.querySelector('.film-toggle');
const label = section.querySelector('.film-toggle-text');
const beats = [...section.querySelectorAll('.beat')];
if (!box || !frame || !toggle || !label || !beats.length) return;

const at = beats.map((b) => Number(b.dataset.at));    // where each beat starts in the film (s)
const still = beats.map((b) => Number(b.dataset.still)); // its representative frame
const LABEL = { play: 'הפעלה', pause: 'עצירה', replay: 'מההתחלה' };
// ~30 fps. Display frames arrive with jitter, so a frame counts as due slightly
// early; otherwise 60 Hz would alternate between drawing every 2nd and 3rd frame.
const FRAME_MS = 0.9 * 1000 / 30;
const reduce = window.matchMedia('(prefers-reduced-motion: reduce)');

let t = 0;            // film time shown, seconds
let playing = false;
let held = false;     // the visitor paused: never resume by scrolling
let inView = false;
let raf = 0, last = 0, drawn = 0;

function draw(time) {
  t = Math.max(0, Math.min(time, film.END));
  frame.innerHTML = film.render(t);
  let k = 0;
  while (k + 1 < at.length && t >= at[k + 1]) k++;
  beats.forEach((b, i) => b.classList.toggle('is-active', i === k));
}

function sync() {
  const state = playing ? 'pause' : t >= film.END ? 'replay' : 'play';
  toggle.dataset.state = state;
  label.textContent = LABEL[state];
}

function tick(now) {
  raf = 0;
  if (!playing) return;
  // A hidden tab stops frames; cap the step so the film resumes where it was.
  t += Math.min(now - last, 100) / 1000;
  last = now;
  if (t >= film.END) { playing = false; sync(); }
  if (!playing || now - drawn >= FRAME_MS) { drawn = now; draw(t); }
  if (playing) raf = window.requestAnimationFrame(tick);
}

function play() {
  if (playing) return;
  if (t >= film.END) t = 0;
  playing = true;
  last = performance.now();
  sync();
  raf = window.requestAnimationFrame(tick);
}

function pause() {
  playing = false;
  if (raf) window.cancelAnimationFrame(raf);
  raf = 0;
  sync();
}

// Caption pills sized to the real text in the page font.
const pen = document.createElement('canvas').getContext?.('2d');
if (pen) film.setMeasure((text, size, weight) => { pen.font = `${weight} ${size}px Yahad, system-ui, sans-serif`; return pen.measureText(text).width; });

const autoplay = () => inView && !held && !reduce.matches && t < film.END;

toggle.addEventListener('click', () => {
  if (playing) { held = true; pause(); } else { held = false; play(); }
});

beats.forEach((beat, i) => {
  const heading = beat.querySelector('h3');
  if (!heading) return;
  const go = document.createElement('button');
  go.type = 'button';
  go.className = 'beat-go';
  go.append(...heading.childNodes);
  heading.append(go);
  go.addEventListener('click', () => {
    held = false;
    draw(reduce.matches && !playing ? still[i] : at[i]);
    sync();
    // Below the film on narrow screens: bring it back; it plays once in view.
    if (!inView) frame.scrollIntoView({ block: 'nearest' });
    else if (!reduce.matches || playing) play();
  });
});

new window.IntersectionObserver(([entry]) => {
  inView = entry.isIntersecting && entry.intersectionRatio >= 0.5;
  if (!inView) pause();
  else if (autoplay()) play();
}, { threshold: [0, 0.5] }).observe(frame);

const onReduce = () => {
  if (!reduce.matches) { if (autoplay()) play(); return; }
  pause();
  draw(still[Math.max(0, beats.findIndex((b) => b.classList.contains('is-active')))]);
  sync();
};
if (reduce.addEventListener) reduce.addEventListener('change', onReduce);
else if (reduce.addListener) reduce.addListener(onReduce);

box.hidden = false;
section.classList.add('film-live');
draw(reduce.matches ? still[0] : 0);
sync();
})();
