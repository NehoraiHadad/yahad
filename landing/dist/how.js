(() => {
  'use strict';

  const section = document.getElementById('how-it-works');
  if (!section || !window.CSS?.supports('position', 'sticky')) return;

  const stage = section.querySelector('.how-stage');
  const counter = section.querySelector('.how-counter');
  const captions = [...section.querySelectorAll('.how-cap')];
  if (!stage || !counter || !captions.length) return;

  const motion = window.matchMedia('(prefers-reduced-motion: no-preference) and (min-height: 540px)');
  section.style.setProperty('--step-count', captions.length);

  let frame = 0;
  let active = -1;
  let distance = 0;

  function render() {
    frame = 0;
    if (!distance) return;

    const scroll = Math.max(0, -section.getBoundingClientRect().top);
    const position = Math.min(captions.length, scroll / distance * captions.length);
    const step = Math.min(captions.length - 1, Math.floor(position));

    if (step !== active) {
      captions[active]?.classList.remove('is-current');
      captions[step].classList.add('is-current');
      section.dataset.step = String(step + 1);
      counter.textContent = `שלב ${step + 1} מתוך ${captions.length}`;
      active = step;
    }

    // The transfer reverses with the page. A blocked chat stays in the middle.
    const progress = position - step;
    const travel = step === 4 ? progress : step === 6 ? 1 - progress : 0.5;
    section.toggleAttribute('data-arrived', (step === 4 || step === 6) && progress >= 0.75);
    section.style.setProperty('--travel', `${travel * 100}%`);
  }

  function configure() {
    if (motion.matches) {
      section.dataset.scroll = '';
      const stageHeight = stage.offsetHeight;
      if (stageHeight <= window.innerHeight) {
        distance = section.clientHeight - stageHeight;
        if (distance > 0) {
          if (!frame) render();
          return;
        }
      }
    }

    distance = 0;
    delete section.dataset.scroll;
    delete section.dataset.step;
    section.removeAttribute('data-arrived');
    captions[active]?.classList.remove('is-current');
    active = -1;
  }

  window.addEventListener('scroll', () => {
    if (distance && !frame) frame = window.requestAnimationFrame(render);
  }, { passive: true });
  window.addEventListener('resize', configure);
  motion.addEventListener('change', configure);
  document.fonts?.ready.then(configure);
  configure();
})();
