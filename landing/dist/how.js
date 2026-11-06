(() => {
  'use strict';

  const section = document.getElementById('how-it-works');
  if (!section || !window.CSS?.supports('position', 'sticky')) return;

  const find = selector => section.querySelector(selector);
  const story = find('.how-story');
  const demo = find('.how-demo');
  const scene = find('.how-scene');
  const label = find('.how-scene-label');
  const typed = find('.typed');
  const chapters = [...section.querySelectorAll('.how-chapter')];
  const dots = [...section.querySelectorAll('.how-dots i')];
  const switches = [...section.querySelectorAll('[data-approval]')];
  const pins = [...section.querySelectorAll('.pin i')];
  const devices = [
    { slot: find('.how-phone-slot'), element: find('.dev-phone'), width: 280, height: 580, turn: -10 },
    { slot: find('.how-tablet-slot'), element: find('.dev-tablet'), width: 560, height: 392, turn: 9 },
  ];
  if (!story || !demo || !scene || !label || !typed || !chapters.length || devices.some(device => !device.slot || !device.element)) return;

  section.dataset.scroll = '';
  if (getComputedStyle(demo).position !== 'sticky') {
    delete section.dataset.scroll;
    return;
  }

  // The seven scenes are paced by the three written chapters, without a scroll spacer.
  const beats = [
    { name: 'connect', label: 'חיבור החשבון', focus: -1 },
    { name: 'approve', label: 'בוחרים שיחות', focus: -1 },
    { name: 'blocked', label: 'שיחה שלא אושרה', focus: 0 },
    { name: 'signin', label: 'נועה נכנסת', focus: 1 },
    { name: 'photo', label: 'התמונה מגיעה לנועה', focus: 0 },
    { name: 'type', label: 'נועה מקלידה תשובה', focus: 1 },
    { name: 'reply', label: 'התשובה מגיעה לסבתא', focus: 0 },
  ];
  const reply = Array.from('וואו! איזה יפים 😍');
  const motion = window.matchMedia('(prefers-reduced-motion: reduce)');
  const clamp = value => Math.max(0, Math.min(1, value));
  const ease = value => value * value * (3 - 2 * value);
  const between = (value, start, end) => clamp((value - start) / (end - start));
  const mix = (from, to, progress) => from + (to - from) * progress;
  let queued = false;
  let activeChapter = -1;
  let activeBeat = -1;
  let lastProgress = -1;
  let size = '';

  function drawCamera(focus, time) {
    const width = scene.clientWidth;
    const height = scene.clientHeight;
    const strength = Math.abs(focus);
    // Clear the second screen before the close-up moves toward center.
    const zoom = ease(between(strength, 0.30, 1));
    devices.forEach((device, index) => {
      const foreground = index === (focus < 0 ? 0 : 1);
      const closeUp = foreground ? zoom : 0;
      const slotWidth = device.slot.offsetWidth;
      const wideScale = Math.min((slotWidth - 12) / device.width, (height - 36) / device.height, 0.73);
      const nearScale = Math.min((width - 38) / device.width, (height - 19) / device.height, 0.84);
      const offset = width / 2 - (device.slot.offsetLeft + slotWidth / 2);
      const bob = Math.sin(time * Math.PI * 2 + index * Math.PI) * 3;
      const yaw = mix(device.turn + Math.sin(time * Math.PI * 2) * 5, index === 0 ? 7 : -6, closeUp);
      const scale = Math.max(0.05, mix(wideScale, nearScale, closeUp));
      device.element.style.transform = `translate(-50%, -50%) translate3d(${(offset * closeUp).toFixed(1)}px, ${bob.toFixed(1)}px, 0) scale(${scale.toFixed(3)}) rotateX(${(4 + 3 * closeUp).toFixed(1)}deg) rotateY(${yaw.toFixed(1)}deg)`;
      device.element.style.opacity = foreground ? '1' : String(1 - ease(between(strength, 0, 0.30)));
    });
    scene.style.setProperty('--bridge-opacity', String(1 - ease(between(strength, 0.16, 0.38))));
    scene.style.setProperty('--sheen', `${50 + 29 * Math.sin(time * Math.PI * 2)}%`);
  }

  function draw(beat, progress) {
    const reduced = motion.matches;
    const linked = beat !== 0 || progress >= 0.46;
    const photoArrived = beat > 4 || (beat === 4 && progress >= 0.68);
    const sent = beat === 6 && progress >= 0.20;
    const delivered = beat === 6 && progress >= 0.80;
    section.toggleAttribute('data-linked', linked);
    section.toggleAttribute('data-photo', photoArrived);
    section.toggleAttribute('data-sent', sent);
    section.toggleAttribute('data-reply', delivered);
    switches.forEach((control, index) => control.classList.toggle('on', beat > 1 || (beat === 1 && progress >= 0.2 + index * 0.22)));
    pins.forEach((dot, index) => dot.classList.toggle('filled', beat === 3 && progress >= 0.18 + index * 0.15));
    section.toggleAttribute('data-press', beat === 3 && progress >= 0.80 && progress <= 0.95);
    section.toggleAttribute('data-send-press', beat === 6 && progress >= 0.13 && progress <= 0.24);
    const typedCount = beat === 5 ? Math.round(reply.length * between(progress, 0.12, 0.82)) : beat === 6 && !sent ? reply.length : 0;
    const input = reply.slice(0, typedCount).join('');
    if (typed.textContent !== input) typed.textContent = input;

    let packet = '';
    let distance = 0.5;
    let flash = 0;
    if (beat === 0) {
      flash = Math.sin(Math.PI * between(progress, 0.54, 1));
    } else if (beat === 2) {
      packet = 'private';
      distance = 0.48 * (ease(between(progress, 0.20, 0.44)) - ease(between(progress, 0.62, 1)));
      flash = Math.sin(Math.PI * progress);
    } else if (beat === 4 || beat === 6) {
      packet = beat === 4 ? 'photo' : 'reply';
      const path = ease(between(progress, 0.20, beat === 4 ? 0.68 : 0.80));
      distance = beat === 4 ? path : 1 - path;
      flash = Math.sin(Math.PI * path);
    }
    section.dataset.transfer = packet;
    section.toggleAttribute('data-lock', beat === 2 && progress >= 0.36 && progress <= 0.86);
    scene.style.setProperty('--travel', `${-100 * distance}%`);
    scene.style.setProperty('--signal', String(flash));

    if (reduced) {
      devices.forEach(({ element }) => {
        element.style.removeProperty('transform');
        element.style.removeProperty('opacity');
      });
      return;
    }
    const from = beats[beat - 1]?.focus ?? 0;
    const to = beats[beat + 1]?.focus ?? 0;
    // Wide beats stay wide during the actual packet transfer.
    const target = beats[beat].focus;
    const focus = progress < 0.20
      ? mix((from + target) / 2, target, ease(progress / 0.20))
      : mix(target, (target + to) / 2, ease(between(progress, 0.84, 1)));
    // Open the shot briefly so the connection lights up between both screens.
    const linkReveal = beat === 0 ? ease(between(progress, 0.55, 0.80)) * (1 - ease(between(progress, 0.88, 1))) : 0;
    drawCamera(focus * (1 - linkReveal), (beat + progress) / beats.length);
  }

  function render() {
    queued = false;
    const viewport = document.documentElement.clientHeight;
    const panel = demo.getBoundingClientRect();
    const stacked = demo.offsetWidth === story.offsetWidth;
    const stickyTop = Number.parseFloat(getComputedStyle(demo).top) || 0;
    const readingLine = stacked
      ? Math.min(viewport - 26, Math.max(0, panel.bottom) + 48)
      : Math.min(viewport * 0.58, stickyTop + panel.height * 0.78);
    const inset = stacked ? 0 : 24;

    let chapter = 0;
    let bounds = chapters[0].getBoundingClientRect();
    for (let i = 1; i < chapters.length; i++) {
      const next = chapters[i].getBoundingClientRect();
      if (next.top + inset > readingLine) break;
      chapter = i;
      bounds = next;
    }
    if (chapter !== activeChapter) {
      chapters[activeChapter]?.removeAttribute('aria-current');
      chapters[chapter].setAttribute('aria-current', 'step');
      activeChapter = chapter;
    }

    const length = Math.min(bounds.height, chapter === 0 ? 110 : 196);
    const advance = clamp((readingLine - bounds.top - inset) / length);
    const local = chapter === 0 ? 0 : Math.min(2, Math.floor(advance * 3));
    const index = chapter === 0 ? 0 : (chapter - 1) * 3 + local + 1;
    const progress = chapter === 0 ? advance : advance * 3 - local;
    const currentSize = `${scene.clientWidth}:${scene.clientHeight}:${motion.matches}`;
    if (index === activeBeat && progress === lastProgress && size === currentSize) return;
    if (index !== activeBeat) {
      dots[activeBeat]?.classList.remove('on');
      dots[index]?.classList.add('on');
      section.dataset.beat = beats[index].name;
      label.textContent = beats[index].label;
      activeBeat = index;
    }
    lastProgress = progress;
    size = currentSize;
    draw(index, progress);
  }

  function scheduleRender() {
    if (queued) return;
    queued = true;
    window.requestAnimationFrame(render);
  }

  render();
  window.addEventListener('scroll', scheduleRender, { passive: true });
  window.addEventListener('resize', scheduleRender);
  if (motion.addEventListener) motion.addEventListener('change', scheduleRender);
  else motion.addListener(scheduleRender);
  document.fonts?.ready.then(scheduleRender);
})();
