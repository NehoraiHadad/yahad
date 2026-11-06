(() => {
  'use strict';

  const section = document.getElementById('how-it-works');
  if (!section || !window.CSS?.supports('position', 'sticky')) return;

  const story = section.querySelector('.how-story');
  const demo = section.querySelector('.how-demo');
  const label = section.querySelector('.how-scene-label');
  const chapters = [...section.querySelectorAll('.how-chapter')];
  if (!story || !demo || !label || !chapters.length) return;

  section.dataset.scroll = '';
  if (getComputedStyle(demo).position !== 'sticky') {
    delete section.dataset.scroll;
    return;
  }

  let queued = false;
  let active = -1;
  let travel = '';

  function render() {
    queued = false;
    const viewport = document.documentElement.clientHeight;
    const panel = demo.getBoundingClientRect();
    const stacked = demo.offsetWidth === story.offsetWidth;
    const stickyTop = Number.parseFloat(getComputedStyle(demo).top) || 0;

    // In the stacked layout, follow the first lines visible below the visual.
    // On desktop the reading line sits within the panel so the reply is visible before it leaves.
    const readingLine = stacked
      ? Math.min(viewport - 26, Math.max(0, panel.bottom) + 48)
      : Math.min(viewport * 0.58, stickyTop + panel.height * 0.78);
    const chapterInset = stacked ? 0 : 24;

    let chapter = 0;
    let bounds = chapters[0].getBoundingClientRect();
    for (let i = 1; i < chapters.length; i++) {
      const next = chapters[i].getBoundingClientRect();
      if (next.top + chapterInset > readingLine) break;
      chapter = i;
      bounds = next;
    }

    if (chapter !== active) {
      chapters[active]?.removeAttribute('aria-current');
      chapters[chapter].setAttribute('aria-current', 'step');
      label.textContent = chapters[chapter].dataset.sceneLabel;
      section.dataset.chapter = String(chapter + 1);
      active = chapter;
    }

    const travelLength = Math.min(bounds.height, 110);
    const progress = Math.max(0, Math.min(1, (readingLine - bounds.top - chapterInset) / travelLength));
    const conversation = chapter === chapters.length - 1;
    const photoArrived = conversation && progress >= 0.39;
    const replyArrived = conversation && progress >= 0.77;
    const transfer = !conversation || replyArrived ? '' : progress < 0.39 ? 'photo' : 'reply';
    section.toggleAttribute('data-photo', photoArrived);
    section.toggleAttribute('data-reply', replyArrived);
    if (section.dataset.transfer !== transfer) section.dataset.transfer = transfer;

    const distance = transfer === 'photo' ? progress / 0.39 : transfer === 'reply' ? (0.77 - progress) / 0.38 : 0.5;
    const position = `${Math.round(-1000 * distance) / 10}%`;
    if (position !== travel) {
      section.style.setProperty('--travel', position);
      travel = position;
    }
  }

  function scheduleRender() {
    if (queued) return;
    queued = true;
    window.requestAnimationFrame(render);
  }

  render();
  window.addEventListener('scroll', scheduleRender, { passive: true });
  window.addEventListener('resize', scheduleRender);
  document.fonts?.ready.then(scheduleRender);
})();
