import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { emojiCatalog, emojiCategories, searchEmoji, rememberEmoji, insertEmoji } from '../web/emojiCatalog';
import { EmojiStickerPicker } from '../web/EmojiStickerPicker';
import { loadStickerFile } from '../web/stickerFile';

test('Unicode 17 catalog covers all nine categories without duplicate bases', () => {
  assert.equal(emojiCatalog.length, 1914);
  assert.equal(new Set(emojiCatalog.map(e => e.emoji)).size, emojiCatalog.length);
  for (const [category] of emojiCategories) assert.ok(searchEmoji('', category).length > 50);
  assert.ok(emojiCatalog.some(e => e.name === 'orca'));
});
test('search supports names, Hebrew keyword bridge, native emoji, and categories', () => {
  assert.ok(searchEmoji('לב').some(e => e.emoji === '❤️'));
  assert.ok(searchEmoji('חתול').some(e => e.emoji === '🐈'));
  assert.ok(searchEmoji('grinning FACE').some(e => e.emoji === '😀'));
  assert.ok(searchEmoji('😀').some(e => e.emoji === '😀'));
  assert.equal(searchEmoji('grinning', 'flags').length, 0);
  assert.equal(searchEmoji('no-such-emoji-abc').length, 0);
});
test('recents are bounded, valid, deduplicated, and most recent first', () => {
  let recents: string[] = [];
  for (const entry of emojiCatalog.slice(0, 40)) recents = rememberEmoji(recents, entry.emoji);
  assert.equal(recents.length, 30);
  const reused = recents[10]; recents = rememberEmoji(recents, reused);
  assert.equal(recents[0], reused); assert.equal(new Set(recents).size, 30);
  assert.deepEqual(rememberEmoji(recents, '<script>'), recents);
  assert.deepEqual(searchEmoji('', 'recent', ['invalid', reused]).map(e => e.emoji), [reused]);
});
test('emoji insertion preserves caret selection and complex unicode', () => {
  assert.deepEqual(insertEmoji('שלום עולם', '❤️', 5, 9), { text: 'שלום ❤️', caret: 7 });
  assert.deepEqual(insertEmoji('a', '👩‍💻', null, null), { text: 'a👩‍💻', caret: 6 });
});
test('picker exposes dialog/search/close with bounded initial DOM and gated sticker controls', () => {
  const html = renderToStaticMarkup(React.createElement(EmojiStickerPicker, { onEmoji() {}, onClose() {} }));
  assert.match(html, /role="dialog"/); assert.match(html, /dir="rtl"/); assert.match(html, /type="search"/);
  assert.match(html, /סגירת בורר/); assert.match(html, /הצגת אימוג׳י נוספים/);
  assert.ok((html.match(/title=/g) || []).length <= 80);
  assert.doesNotMatch(html, /מדבקה מקובץ/);
});

test('sticker helper rejects external paths without fetching', async () => {
  for (const url of ['https://example.com/a.webp', '//example.com/a.webp', '/api/groups/g/media/m?redirect=x', '/other']) {
    await assert.rejects(loadStickerFile({ id: 'm', label: 'synthetic', url }, 100, new AbortController().signal), /כתובת/);
  }
});
test('sticker helper validates mime, signature, max bytes, and cancellation', async t => {
  const valid = Uint8Array.from(Buffer.from('RIFFxxxxWEBPsynthetic'));
  const sticker = { id: 'm', label: 'synthetic', url: '/api/groups/synthetic/media/m' };
  const signal = new AbortController().signal;
  t.mock.method(globalThis, 'fetch', async (_url: RequestInfo | URL, init?: RequestInit) => {
    assert.equal(init?.redirect, 'error'); assert.equal(init?.credentials, 'same-origin');
    return new Response(valid, { headers: { 'content-type': 'image/webp' } });
  });
  const file = await loadStickerFile(sticker, 100, signal);
  assert.equal(file.type, 'image/webp'); assert.equal(file.size, valid.length);
  await assert.rejects(loadStickerFile(sticker, 12, signal), /גדולה/);
  t.mock.method(globalThis, 'fetch', async () => new Response('notwebp', { headers: { 'content-type': 'image/webp' } }));
  await assert.rejects(loadStickerFile(sticker, 100, signal), /אינו WebP/);
  t.mock.method(globalThis, 'fetch', async () => new Response(valid, { headers: { 'content-type': 'text/html' } }));
  await assert.rejects(loadStickerFile(sticker, 100, signal), /רק מדבקה/);
  const aborted = new AbortController(); aborted.abort();
  t.mock.method(globalThis, 'fetch', async () => new Response(valid, { headers: { 'content-type': 'image/webp' } }));
  await assert.rejects(loadStickerFile(sticker, 100, aborted.signal), { name: 'AbortError' });
});

test('official tone variants preserve ZWJ sequences and support mixed tones, search, and recents', async () => {
  const { emojiVariants } = await import('../web/emojiVariants');
  assert.ok(emojiVariants['👋'].includes('👋🏽'));
  assert.ok(emojiVariants['👩‍💻'].includes('👩🏽‍💻'));
  assert.ok(emojiVariants['🤝'].includes('🫱🏻‍🫲🏿'));
  assert.ok(emojiVariants['💏'].some(value => value.includes('🧑🏻')));
  assert.equal(new Set(Object.values(emojiVariants).flat()).size, 2030);
  assert.equal(searchEmoji('👩🏽‍💻')[0].emoji, '👩🏽‍💻');
  assert.deepEqual(rememberEmoji([], '👩🏽‍💻'), ['👩🏽‍💻']);
  assert.equal(searchEmoji('', 'recent', ['👩🏽‍💻'])[0].emoji, '👩🏽‍💻');
});
