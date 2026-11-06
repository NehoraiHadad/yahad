(() => {
  'use strict';

  const section = document.getElementById('how-it-works');
  if (!section || !window.CSS?.supports('position', 'sticky')) return;

  const track = section.querySelector('.how-scroll-track');
  const counter = section.querySelector('.how-counter');
  const captions = [...section.querySelectorAll('.how-cap')];
  if (!track || !counter || !captions.length) return;

  section.style.setProperty('--step-count', captions.length);
  section.dataset.scroll = '';
  if (!track.offsetHeight) {
    delete section.dataset.scroll;
    return;
  }

  let queued = false;
  let active = -1;
  let lastPosition = -1;

  function render() {
    queued = false;
    const distance = track.offsetHeight;
    if (!distance) return;

    const scroll = Math.max(0, -section.getBoundingClientRect().top - section.clientTop);
    const position = Math.min(captions.length, scroll / distance * captions.length);
    if (position === lastPosition) return;
    lastPosition = position;

    const step = Math.min(captions.length - 1, Math.floor(position));
    if (step !== active) {
      captions[active]?.classList.remove('is-current');
      captions[step].classList.add('is-current');
      section.dataset.step = String(step + 1);
      counter.textContent = `שלב ${step + 1} מתוך ${captions.length}`;
      active = step;
    }

    // Map the outgoing photo and incoming reply to opposite directions.
    const progress = position - step;
    const travel = step === 4 ? progress : step === 6 ? 1 - progress : 0.5;
    section.toggleAttribute('data-arrived', (step === 4 || step === 6) && progress >= 0.75);
    section.style.setProperty('--travel', `${travel * -100}%`);
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
