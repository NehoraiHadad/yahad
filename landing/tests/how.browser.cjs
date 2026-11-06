const assert = require('node:assert/strict');
const { chromium } = require('playwright');

const url = new URL('#how-it-works', process.argv[2] || 'http://127.0.0.1:4173/');
const viewports = [
  { width: 320, height: 480 },
  { width: 360, height: 520 },
  { width: 393, height: 659 },
  { width: 390, height: 844, reducedMotion: 'reduce' },
  { width: 740, height: 360 },
  { width: 1440, height: 900 },
];

const beats = ['connect', 'approve', 'blocked', 'signin', 'photo', 'type', 'reply'];

async function scrollToBeat(page, beat, progress = 0.5) {
  const index = beats.indexOf(beat);
  const chapter = index === 0 ? 1 : index <= 3 ? 2 : 3;
  const distance = index === 0 ? progress * 110 : (index - (chapter === 2 ? 1 : 4) + progress) / 3 * 196;
  await page.evaluate(({ chapter, distance }) => {
    const demo = document.querySelector('.how-demo');
    const story = document.querySelector('.how-story');
    const target = story.querySelectorAll('.how-chapter')[chapter - 1];
    const top = Number.parseFloat(getComputedStyle(demo).top) || 0;
    const stacked = demo.offsetWidth === story.offsetWidth;
    const readingLine = stacked
      ? Math.min(innerHeight - 26, top + demo.offsetHeight + 48)
      : Math.min(innerHeight * 0.58, top + demo.offsetHeight * 0.78) - 24;
    scrollTo({ top: scrollY + target.getBoundingClientRect().top + distance - readingLine, behavior: 'instant' });
  }, { chapter, distance });
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await page.waitForFunction(
    expected => document.querySelector('.how').dataset.beat === expected, beat, { timeout: 2000 },
  );
}

function measure(page) {
  return page.evaluate(() => {
    const section = document.querySelector('.how');
    const rect = selector => section.querySelector(selector).getBoundingClientRect().toJSON();
    const captions = [...section.querySelectorAll('.how-chapter')];
    const packet = [...section.querySelectorAll('.how-packet')].find(el => getComputedStyle(el).display !== 'none');
    const visible = selector => getComputedStyle(section.querySelector(selector)).visibility === 'visible';
    return {
      viewportHeight: innerHeight,
      demo: rect('.how-demo'), phone: rect('.dev-phone'), tablet: rect('.dev-tablet'), bridge: rect('.how-bridge'),
      packet: packet?.querySelector('svg, .packet-mark').getBoundingClientRect().toJSON(),
      scene: rect('.how-scene'), phoneOpacity: Number(getComputedStyle(section.querySelector('.dev-phone')).opacity),
      tabletOpacity: Number(getComputedStyle(section.querySelector('.dev-tablet')).opacity),
      linked: section.hasAttribute('data-linked'), lock: section.hasAttribute('data-lock'),
      badge: getComputedStyle(section.querySelector('.message-badge')).display !== 'none',
      switches: section.querySelectorAll('[data-approval].on').length,
      pins: section.querySelectorAll('.pin .filled').length,
      typed: section.querySelector('.typed').textContent,
      captionCount: captions.filter(el => getComputedStyle(el).opacity === '1').length,
      activeCount: captions.filter(el => el.getAttribute('aria-current') === 'step').length,
      sticky: getComputedStyle(section.querySelector('.how-demo')).position === 'sticky',
      demoHidden: getComputedStyle(section.querySelector('.how-demo')).display === 'none',
      overflow: document.documentElement.scrollWidth > innerWidth,
      photoVisible: visible('.kid-photo'),
      replyVisible: visible('.wa-reply'),
      childReplyVisible: visible('.kid-reply'),
    };
  });
}

async function swipeUp(page) {
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: 180, y: 400 }] });
  for (let y = 370; y >= 100; y -= 30) {
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: 180, y }] });
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(resolve)));
  }
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await page.waitForFunction(() => document.querySelector('.how').dataset.beat !== 'connect', null, { timeout: 2500 });
}

async function withPage(options, test) {
  const executablePath = process.env.YAHAD_CHROMIUM_EXECUTABLE;
  const args = process.env.YAHAD_CHROMIUM_SINGLE_PROCESS === '1' ? ['--single-process', '--no-zygote'] : [];
  const browser = await chromium.launch({ executablePath, args });
  try {
    await test(await browser.newPage(options));
  } finally {
    await browser.close();
  }
}

async function main() {
  for (const { width, height, reducedMotion } of viewports) {
    const isMobile = width < 800;
    const label = `${width}×${height}${reducedMotion ? ' reduced motion' : ''}`;
    await withPage({ viewport: { width, height }, isMobile, hasTouch: isMobile, reducedMotion }, async page => {
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      await page.goto(url.href);
      await page.evaluate(() => document.fonts.ready);
      for (const [beat, progress] of beats.flatMap(beat => [0.12, 0.5, 0.92].map(progress => [beat, progress]))) {
        await scrollToBeat(page, beat, progress);
        const view = await measure(page);
        assert.ok(view.sticky && !view.overflow, label);
        assert.equal(view.activeCount, 1, label);
        assert.equal(view.captionCount, 3, label);
        for (const [device, opacity] of [[view.tablet, view.tabletOpacity], [view.phone, view.phoneOpacity]]) {
          if (opacity > 0.05) {
            assert.ok(device.left >= view.scene.left - 1 && device.right <= view.scene.right + 1, `${label} ${beat} horizontal crop`);
            assert.ok(device.top >= view.scene.top - 1 && device.bottom <= view.scene.bottom + 1, `${label} ${beat} vertical crop`);
          }
        }
        if (view.tabletOpacity > 0.05 && view.phoneOpacity > 0.05) assert.ok(view.tablet.right <= view.phone.left, label);
        if (view.packet) assert.ok(view.packet.left >= view.bridge.left - 1 && view.packet.right <= view.bridge.right + 1, label);
      }

      await scrollToBeat(page, 'connect', 0.18);
      assert.equal((await measure(page)).linked, false, label);
      await scrollToBeat(page, 'approve', 0.45);
      assert.equal((await measure(page)).switches, 2, label);
      await scrollToBeat(page, 'blocked', 0.52);
      const stopped = await measure(page);
      assert.equal(stopped.lock, true, label);
      await scrollToBeat(page, 'blocked', 0.94);
      const bounced = await measure(page);
      if (!reducedMotion) assert.ok(bounced.packet.left > stopped.packet.left, label);
      await scrollToBeat(page, 'signin', 0.76);
      assert.equal((await measure(page)).pins, 4, label);
      await scrollToBeat(page, 'photo', 0.36);
      const earlyPhoto = await measure(page);
      await scrollToBeat(page, 'photo', 0.56);
      const latePhoto = await measure(page);
      if (reducedMotion) assert.equal(earlyPhoto.packet.left, latePhoto.packet.left, label);
      else assert.ok(latePhoto.packet.left < earlyPhoto.packet.left, label);
      await scrollToBeat(page, 'photo', 0.76);
      assert.equal((await measure(page)).badge, true, label);
      await scrollToBeat(page, 'type', 0.60);
      assert.ok((await measure(page)).typed.startsWith('וואו'), label);
      await scrollToBeat(page, 'reply', 0.55);
      const replyInTransit = await measure(page);
      assert.equal(replyInTransit.childReplyVisible, true, label);
      assert.equal(replyInTransit.replyVisible, false, label);
      await scrollToBeat(page, 'reply', 0.88);
      assert.equal((await measure(page)).replyVisible, true, label);
      await scrollToBeat(page, 'connect', 0.25);
      if (height === 520) {
        await swipeUp(page);
        await page.setViewportSize({ width, height: 480 });
        await scrollToBeat(page, 'blocked');
        assert.equal((await measure(page)).activeCount, 1, 'resize keeps the story active');
      }
      const faq = page.locator('#faq');
      await faq.scrollIntoViewIfNeeded();
      assert.ok(await faq.evaluate(el => el.getBoundingClientRect().top < innerHeight), label);
      assert.deepEqual(errors, [], label);
    });
    console.log(`PASS: ${label}, seven beats, camera bounds, transfers, reverse scrolling and exit`);
  }

  for (const javaScriptEnabled of [false, true]) {
    await withPage({ viewport: { width: 360, height: 520 }, isMobile: true, javaScriptEnabled }, async page => {
      if (javaScriptEnabled) await page.route('**/how.css?*', route => route.abort());
      await page.goto(url.href);
      const view = await measure(page);
      assert.equal(view.captionCount, 3, 'the written story remains readable');
      assert.equal(view.sticky, false);
      assert.equal(view.demoHidden, javaScriptEnabled);
      if (!javaScriptEnabled) assert.equal(view.replyVisible, true, 'fallback shows a finished conversation');
    });
  }
  console.log('PASS: touch swipe, viewport resize, and missing JavaScript or stylesheet fallback');
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
