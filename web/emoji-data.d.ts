declare module 'unicode-emoji-json/data-by-group.json' {
  const groups: { name: string; slug: string; emojis: { emoji: string; name: string; slug: string; skin_tone_support: boolean }[] }[];
  export default groups;
}
