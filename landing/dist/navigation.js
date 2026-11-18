(() => {
  const details = document.getElementById('technical');
  const nav = document.querySelector('.masthead nav');
  if (!details || !nav) return;

  function reveal(hash) {
    const destination = document.getElementById(hash.slice(1));
    if (details.open || !destination || !details.contains(destination) || destination === details) return;
    details.open = true;
    return destination;
  }

  function followCurrentHash() {
    reveal(window.location.hash)?.scrollIntoView();
  }

  nav.addEventListener('click', (event) => {
    if (event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    const link = event.target.closest('a[href^="#"]');
    if (link) reveal(link.hash);
  });

  window.addEventListener('hashchange', followCurrentHash);
  followCurrentHash();
})();
