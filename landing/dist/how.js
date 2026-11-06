(() => {
  'use strict';

  const section = document.getElementById('how-it-works');
  if (!section) return;

  const captions = [...section.querySelectorAll('.how-cap')];
  const progress = section.querySelector('.how-progress');
  const controls = section.querySelector('.how-controls');
  const previous = section.querySelector('.how-prev');
  const next = section.querySelector('.how-next');
  const status = section.querySelector('[role="status"]');
  if (!captions.length || !progress || !controls || !previous || !next || !status) return;

  let step = 0;
  const lastStep = captions.length - 1;

  function showStep(index, announce = true) {
    step = index;
    section.dataset.step = step + 1;
    captions.forEach((caption, i) => {
      caption.setAttribute('aria-hidden', String(i !== step));
    });
    const position = `שלב ${step + 1} מתוך ${captions.length}`;
    progress.textContent = position;
    previous.setAttribute('aria-disabled', String(step === 0));
    next.textContent = step === lastStep ? 'מההתחלה' : 'הבא';
    if (announce) {
      const caption = captions[step];
      status.textContent = `${position}. ${caption.querySelector('h3').textContent}. ${caption.querySelector('p').textContent}`;
    }
  }

  previous.addEventListener('click', () => {
    if (step > 0) showStep(step - 1);
  });
  next.addEventListener('click', () => showStep(step === lastStep ? 0 : step + 1));

  showStep(0, false);
  progress.hidden = false;
  controls.hidden = false;
})();
