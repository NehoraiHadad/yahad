/**
 * Offline synthetic geometry fixture for a permitted browser (no API/provider).
 * node --import tsx tests/admin-layout-browser.ts > /tmp/yachad-admin-layout.html
 * Open that file at desktop, tablet and mobile viewport widths. The page prints
 * baseline/fixed geometry and PASS/FAIL; generating it does not run a browser.
 */
import { readFileSync } from 'node:fs';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { GroupPicker } from '../web/GroupPicker.js';
const css = readFileSync(new URL('../web/style.css', import.meta.url), 'utf8');
const picker = renderToStaticMarkup(React.createElement(GroupPicker, {
  groups: Array.from({ length: 180 }, (_, i) => ({ id: `synthetic-${i}@g.us`, title: `קבוצה לבדיקה ${i}` })),
  grants: [], childId: 'synthetic-child', busy: false, loading: false, onToggle() {},
}));
console.log(`<!doctype html><html lang="he" dir="rtl"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Synthetic admin scroll regression</title><style>${css}</style>
<main class="parent-page"><header class="parent-nav">בדיקת גלילה סינתטית</header>
<div class="parent-content"><h1>בדיקת פריסת ניהול</h1><div class="parent-grid">
<section class="parent-card"><h2>חיבור מדומה בלבד</h2><div class="link-illustration">אין חיבור לחשבון אמיתי</div></section>
<section class="parent-card group-picker">${picker}</section></div>
<section class="parent-card next-step"><h2>אחרי רשימת הקבוצות</h2></section>
<div class="danger-zone" id="last-content"><button>סוף התוכן הסינתטי</button></div></div></main>
<pre id="results" dir="ltr" style="position:fixed;bottom:0;left:0;z-index:100;background:white;color:black;max-width:100%;max-height:35vh;overflow:auto;font-size:12px"></pre>
<script>
(async () => {
  const rows = [...document.querySelectorAll('.approval-row')];
  const list = document.querySelector('.scrollable-group-list');
  const results = document.querySelector('#results');
  const frame = () => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
  function measure(name) {
    const footer = document.querySelector('#last-content').getBoundingClientRect();
    const content = document.querySelector('.parent-content');
    const expectedBottom = footer.bottom + scrollY + parseFloat(getComputedStyle(content).paddingBottom);
    return { name, viewport: [innerWidth, innerHeight], documentHeight: document.documentElement.scrollHeight,
      expectedHeight: Math.max(innerHeight, expectedBottom),
      emptyTail: Math.round(document.documentElement.scrollHeight - Math.max(innerHeight, expectedBottom)),
      listHeight: list.clientHeight, listScrollHeight: list.scrollHeight,
      checkboxContainer: rows[179].querySelector('input').offsetParent?.className };
  }
  // Reintroduce only the historical missing containing block, without changing
  // max heights, overflow, other page layout, or native focus behavior.
  rows.forEach(row => row.style.position = 'static');
  await frame(); const before = measure('historical row position:static');
  rows.forEach(row => row.style.removeProperty('position'));
  await frame(); const after = measure('current CSS');
  rows[179].querySelector('input').focus();
  await frame(); const focused = measure('focus last checkbox');
  rows.forEach((row, index) => row.hidden = index !== 179);
  // [hidden] can be overridden by display:flex, so explicitly model filtering.
  rows.forEach((row, index) => row.style.display = index === 179 ? '' : 'none');
  await frame(); const filtered = measure('one matching group');
  rows.forEach(row => { row.hidden = false; row.style.removeProperty('display'); });
  await frame(); const cleared = measure('clear filter');
  const passed = [after, focused, filtered, cleared].every(item => item.emptyTail <= 2)
    && after.listScrollHeight > after.listHeight
    && after.checkboxContainer.includes('approval-row');
  results.textContent = JSON.stringify({ result: passed ? 'PASS' : 'FAIL', reproducedHistoricalOverflow: before.emptyTail > 100,
    measurements: [before, after, focused, filtered, cleared] }, null, 2);
})();
</script></html>`);
