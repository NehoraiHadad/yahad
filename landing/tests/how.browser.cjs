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

async function scrollToStep(page, step, fraction = 0.5) {
  await page.evaluate(({ step, fraction }) => {
    const section = document.querySelector('#how-it-works');
    const track = section.querySelector('.how-scroll-track');
    const start = scrollY + section.getBoundingClientRect().top + section.clientTop;
    scrollTo({ top: start + (step - 1 + fraction) * track.offsetHeight / 7, behavior: 'instant' });
  }, { step, fraction });
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await page.waitForFunction(
    expected => document.querySelector('.how').dataset.step === String(expected), step, { timeout: 2000 },
  );
}

function measure(page) {
  return page.evaluate(() => {
    const section = document.querySelector('.how');
    const rect = selector => section.querySelector(selector).getBoundingClientRect().toJSON();
    const captions = [...section.querySelectorAll('.how-cap')];
    const packet = [...section.querySelectorAll('.how-packet')].find(el => getComputedStyle(el).display !== 'none');
    return {
      stage: rect('.how-stage'), copy: rect('.how-copy'), scene: rect('.how-scene'),
      phone: rect('.dev-phone'), tablet: rect('.dev-tablet'), bridge: rect('.how-bridge'),
      packet: packet?.querySelector('svg').getBoundingClientRect().toJSON(),
      visible: captions.filter(el => getComputedStyle(el).opacity === '1').length,
      overflow: document.documentElement.scrollWidth > innerWidth,
      viewportHeight: innerHeight,
      replyVisible: getComputedStyle(section.querySelector('.wa-reply')).display !== 'none',
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
  await page.waitForFunction(() => Number(document.querySelector('.how').dataset.step) > 3, null, { timeout: 2500 });
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
      assert.equal((await measure(page)).visible, 1, label);

      for (let step = 1; step <= 7; step++) {
        await scrollToStep(page, step);
        const { visible, overflow, stage, viewportHeight, scene, copy, phone, tablet, bridge, packet } = await measure(page);
        assert.equal(visible, 1, label);
        assert.ok(!overflow && Math.abs(stage.top) <= 1 && stage.bottom <= viewportHeight + 1, label);
        if (width < 600) assert.ok(copy.bottom <= scene.top + 1, label);
        if (step >= 5) {
          assert.ok(tablet.right <= bridge.left + 1 && phone.left >= bridge.right - 1, label);
          assert.ok(packet.left >= bridge.left - 1 && packet.right <= bridge.right + 1, label);
        }
      }

      await scrollToStep(page, 7, 0.2);
      const early = await measure(page);
      await scrollToStep(page, 7, 0.9);
      const late = await measure(page);
      assert.equal(early.replyVisible, false, label);
      assert.equal(late.replyVisible, true, label);
      if (reducedMotion) assert.equal(early.packet.left, late.packet.left, label);
      else assert.ok(late.packet.left > early.packet.left, label);
      await scrollToStep(page, 3);
      if (height === 520) {
        await swipeUp(page);
        await page.setViewportSize({ width, height: 480 });
        await scrollToStep(page, 6);
        assert.equal((await measure(page)).visible, 1, 'resize keeps the scroll experience active');
      }
      const faq = page.locator('#faq');
      await faq.scrollIntoViewIfNeeded();
      assert.ok(await faq.evaluate(el => el.getBoundingClientRect().top < innerHeight), label);
      assert.deepEqual(errors, [], label);
    });
    console.log(`PASS: ${label}, all seven steps, reverse scrolling, layout, and exit`);
  }

  for (const javaScriptEnabled of [false, true]) {
    await withPage({ viewport: { width: 360, height: 520 }, isMobile: true, javaScriptEnabled }, async page => {
      if (javaScriptEnabled) await page.route('**/how.css?*', route => route.abort());
      await page.goto(url.href);
      assert.equal((await measure(page)).visible, 7, 'the written story is the fallback');
    });
  }
  console.log('PASS: touch swipe, viewport resize, and missing JavaScript or stylesheet fallback');
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
