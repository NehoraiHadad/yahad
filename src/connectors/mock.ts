import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import type {
  Connector,
  ConnectorHooks,
  Capabilities,
  Outgoing,
  StoredMessage,
  Incoming,
} from './types.js';
export const mockGroups = [
  { id: '120363001@g.us', title: 'המשפחה שלנו' },
  { id: '120363002@g.us', title: 'מועדון היצירה' },
  { id: '120363003@g.us', title: 'יוצאות לטייל' },
  { id: '120363999@g.us', title: 'קבוצה פרטית של ההורה' },
];
export class MockConnector implements Connector {
  readonly capabilities: Capabilities = {
    reply: true,
    reactions: true,
    image: true,
    video: true,
    document: true,
    sticker: true,
    audio: true,
    edit: true,
    delete: true,
    typing: true,
    read: true,
    receipts: true,
    historyRequest: false,
    editWindowMs: 15 * 60_000,
  };
  timers = new Set<ReturnType<typeof setTimeout>>();
  running = false;
  constructor(readonly hooks: ConnectorHooks) {}
  later(fn: () => void, ms: number) {
    const t = setTimeout(() => {
      this.timers.delete(t);
      if (this.running) fn();
    }, ms);
    this.timers.add(t);
  }
  async start() {
    this.running = true;
    this.hooks.state('connected');
  }
  async stop() {
    this.running = false;
    for (const t of this.timers) clearTimeout(t);
    this.timers.clear();
    this.hooks.state('idle');
  }
  async groups() {
    return mockGroups;
  }
  async approved(g: string) {
    if (!this.running || !this.hooks.allowed(g) || this.hooks.getMessage(g, 'sample-1')) return;
    const base = Date.now() - 45 * 60_000;
    const texts = [
      'בוקר טוב לכולן! איזה כיף שיש לנו מקום להיפגש 🌿',
      'חשבתי שנכין משהו ביחד בסוף השבוע. מה דעתכן?',
      'ברור! אני מביאה את כל הצבעים 🎨',
      'מצאתי השראה קטנה בדרך הביתה',
      'איזה יופי! אפשר גם להוסיף עלים ופרחים מיובשים. זו תהיה יצירה משותפת, וכל אחת תוכל לקחת אותה לכיוון שלה. אין דרך אחת נכונה — העיקר ליהנות מהדרך ✨',
      'שלחתי לכן גם את הפרטים בקובץ. נפגשות אחר הצהריים?',
    ];
    for (let i = 0; i < texts.length; i++)
      await this.hooks.incoming({
        id: `sample-${i + 1}`,
        groupId: g,
        sender: i === 2 ? 'אני' : ['סבתא', 'אמא', 'אני', 'דודה'][i % 4],
        fromMe: i === 2,
        time: base + i * 75_000,
        kind: i === 3 ? 'image' : 'text',
        text: texts[i],
        ...(i === 3
          ? {
              bytes: readFileSync(resolve('fixtures/garden.jpg')),
              mime: 'image/jpeg',
              filename: 'רגע-בגינה.jpg',
            }
          : {}),
        status: i === 2 ? 'read' : undefined,
      });
    const examples: [string, string, string][] = [
      ['audio', 'audio.ogg', 'audio/ogg'],
      ['sticker', 'flower.webp', 'image/webp'],
      ['document', 'weekend.pdf', 'application/pdf'],
      ['video', 'garden.mp4', 'video/mp4'],
    ];
    for (let i = 0; i < examples.length; i++) {
      const [kind, file, mime] = examples[i];
      await this.hooks.incoming({
        id: `sample-media-${i}`,
        groupId: g,
        sender: 'אמא',
        fromMe: false,
        time: base + 600_000 + i * 65_000,
        kind: kind as Incoming['kind'],
        text: kind === 'audio' ? '' : kind === 'video' ? 'רגע של שקט בגינה 🌱' : '',
        bytes: readFileSync(resolve('fixtures', file)),
        mime,
        filename: file,
      });
    }
    await this.hooks.incoming({
      id: 'sample-last',
      groupId: g,
      sender: 'סבתא',
      fromMe: false,
      time: Date.now() - 180_000,
      kind: 'text',
      text: 'מחכה כבר לראות מה תכינו 💚',
      replyId: 'sample-3',
    });
  }
  async send(m: Outgoing): Promise<Incoming> {
    if (!this.running) throw new Error('Disconnected');
    const id = m.id || randomUUID();
    this.later(() => this.hooks.update(m.groupId, id, { status: 'delivered' }), 700);
    this.later(() => this.hooks.update(m.groupId, id, { status: 'read' }), 1800);
    this.later(() => this.hooks.typing(m.groupId, true), 2100);
    this.later(() => {
      this.hooks.typing(m.groupId, false);
      void this.hooks.incoming({
        id: randomUUID(),
        groupId: m.groupId,
        sender: 'משתתפת לדוגמה',
        fromMe: false,
        time: Date.now(),
        kind: 'text',
        text: 'קיבלתי, תודה! 🌸',
        replyId: id,
      });
    }, 3500);
    return {
      id,
      groupId: m.groupId,
      sender: 'אני',
      fromMe: true,
      time: Date.now(),
      kind: m.kind,
      text: m.text,
      mime: m.mime,
      filename: m.filename,
      bytes: m.bytes,
      replyId: m.reply?.id,
      status: 'sent',
    };
  }
  async react(g: string, m: StoredMessage, emoji: string) {
    await this.hooks.incoming({
      id: randomUUID(),
      groupId: g,
      sender: 'אני',
      fromMe: true,
      time: Date.now(),
      kind: 'text',
      text: '',
      reaction: { target: m.id, actor: 'self', emoji },
    });
  }
  async edit(g: string, m: StoredMessage, text: string) {
    this.hooks.update(g, m.id, { text, edited: true });
  }
  async delete(g: string, m: StoredMessage) {
    this.hooks.update(g, m.id, { text: '', deleted: true });
  }
  async presence() {}
  async read() {}
  async history(_group: string, _oldest: StoredMessage): Promise<string | void> {}
}
