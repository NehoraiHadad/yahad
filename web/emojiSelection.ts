// Lightweight composer helpers: keep the catalog in the lazy picker chunk.
const segmenter = typeof Intl.Segmenter === 'function' ? new Intl.Segmenter(undefined, { granularity: 'grapheme' }) : null;
function isEmojiSelection(value: string) {
  return value.length <= 64 && (!segmenter || [...segmenter.segment(value)].length === 1) && /\p{Extended_Pictographic}|\p{Regional_Indicator}|[0-9#*]\ufe0f?\u20e3/u.test(value);
}
export function rememberEmoji(previous: readonly string[], emoji: string) {
  return isEmojiSelection(emoji) ? [emoji, ...previous.filter(value => value !== emoji && isEmojiSelection(value))].slice(0, 30) : [...previous];
}
export function insertEmoji(text: string, emoji: string, start: number | null, end: number | null) {
  const from = Math.min(text.length, Math.max(0, start ?? text.length));
  const to = Math.min(text.length, Math.max(from, end ?? from));
  return { text: text.slice(0, from) + emoji + text.slice(to), caret: from + emoji.length };
}
