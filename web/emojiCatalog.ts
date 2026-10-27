import data from 'unicode-emoji-json/data-by-group.json';
import { emojiVariants } from './emojiVariants';
export { emojiVariants } from './emojiVariants';

export const emojiCategories = [
  ['smileys_emotion', 'חיוכים ורגשות', '😀'], ['people_body', 'אנשים וידיים', '👋'],
  ['animals_nature', 'חיות וטבע', '🌿'], ['food_drink', 'אוכל ושתייה', '🍓'],
  ['travel_places', 'נסיעות ומקומות', '🚗'], ['activities', 'ספורט ומשחקים', '⚽'],
  ['objects', 'חפצים', '💡'], ['symbols', 'סמלים', '❤️'], ['flags', 'דגלים', '🏳️'],
] as const;
// Small authored Hebrew keyword bridge. English Unicode names remain searchable for every entry.
const hebrewKeywords: [RegExp, string][] = [
  [/face|smil|grin/, 'פנים חיוך שמח חיוכים'], [/laugh|joy/, 'צחוק מצחיק חחח'],
  [/heart|love|kiss/, 'לב אהבה נשיקה'], [/cry|sad|tear/, 'בכי עצוב דמעה'],
  [/angry|rage/, 'כועס כעס'], [/hand|thumb|clap|fist/, 'יד ידיים אגודל כפיים'],
  [/thumbs up/, 'לייק מעולה'], [/folded hands/, 'תודה תפילה בבקשה'],
  [/flower|blossom|rose|tulip/, 'פרח פרחים'], [/sun|moon|star|cloud|rain/, 'שמש ירח כוכב ענן גשם שמיים'],
  [/dog/, 'כלב'], [/cat/, 'חתול'], [/bird/, 'ציפור'], [/butterfly/, 'פרפר'],
  [/cake|birthday|party|confetti|balloon/, 'יום הולדת חגיגה מסיבה מזל טוב'],
  [/food|bread|pizza|cake|fruit|apple|strawberry/, 'אוכל טעים לחם פיצה עוגה פרי תפוח תות'],
  [/coffee|tea|drink|cup/, 'קפה תה שתייה כוס'], [/ball|sport|soccer/, 'כדור ספורט משחק'],
  [/car|bus|train|airplane|bicycle/, 'רכב מכונית אוטובוס רכבת מטוס אופניים'],
  [/flag/, 'דגל'], [/israel/, 'ישראל'], [/family|child|boy|girl|baby/, 'משפחה ילד ילדה תינוק'],
  [/check|tick/, 'אישור וי'], [/fire/, 'אש'], [/gift/, 'מתנה'], [/sleep/, 'שינה לילה'],
];
export const emojiCatalog = data.flatMap(group => group.emojis.map(entry => ({
  ...entry, category: group.slug,
  search: `${entry.emoji} ${entry.name} ${entry.slug.replaceAll('_', ' ')} ${emojiCategories.find(c => c[0] === group.slug)?.[1] || ''} ${hebrewKeywords.filter(([re]) => re.test(entry.name)).map(([, words]) => words).join(' ')}`.toLowerCase(),
})));
export const emojiByNative = new Map(emojiCatalog.map(entry => [entry.emoji, entry]));
for (const [base, variants] of Object.entries(emojiVariants)) {
  const entry = emojiByNative.get(base);
  if (entry) for (const emoji of variants) emojiByNative.set(emoji, { ...entry, emoji, search: `${entry.search} ${emoji}` });
}
export function searchEmoji(query: string, category = 'all', recents: readonly string[] = []) {
  const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const directVariant = emojiByNative.get(query.trim());
  if (directVariant && !emojiCatalog.some(entry => entry.emoji === query.trim()) && (category === 'all' || category === directVariant.category)) return [directVariant];
  const entries = category === 'recent' ? recents.flatMap(value => emojiByNative.has(value) ? [emojiByNative.get(value)!] : []) : emojiCatalog;
  return entries.filter(entry => (category === 'all' || category === 'recent' || entry.category === category) && words.every(word => entry.search.includes(word)));
}
export { rememberEmoji, insertEmoji } from './emojiSelection';
