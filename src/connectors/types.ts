export type Kind = 'text' | 'image' | 'video' | 'document' | 'sticker' | 'audio' | 'unsupported';
export type Receipt = 'pending' | 'sent' | 'delivered' | 'read' | 'unknown' | 'failed';
export interface Capabilities {
  reply: boolean;
  reactions: boolean;
  image: boolean;
  video: boolean;
  document: boolean;
  sticker: boolean;
  audio: boolean;
  edit: boolean;
  delete: boolean;
  typing: boolean;
  read: boolean;
  receipts: boolean;
  historyRequest: boolean;
  editWindowMs: number;
}
export interface ChatGroup {
  id: string;
  title: string;
}
export interface Incoming {
  id: string;
  groupId: string;
  sender: string;
  fromMe: boolean;
  time: number;
  kind: Kind;
  text: string;
  replyId?: string;
  mime?: string;
  filename?: string;
  raw?: string;
  bytes?: Buffer;
  download?: () => Promise<Buffer>;
  status?: Receipt;
  edited?: boolean;
  deleted?: boolean;
  reaction?: { target: string; actor: string; emoji: string };
}
export interface StoredMessage {
  id: string;
  groupId: string;
  sender: string;
  fromMe: boolean;
  time: number;
  kind: Kind;
  text: string;
  replyId?: string;
  mediaId?: string;
  mime?: string;
  filename?: string;
  status: Receipt;
  edited?: boolean;
  deleted?: boolean;
  reactions: Record<string, string>;
  raw?: string;
  clientId?: string;
  authorUserId?: string;
  authorLabel?: string;
}
export interface Outgoing {
  groupId: string;
  id: string;
  text: string;
  kind: Kind;
  reply?: StoredMessage;
  bytes?: Buffer;
  mime?: string;
  filename?: string;
}
export type ConnectionState =
  | 'idle'
  | 'connecting'
  | 'qr'
  | 'connected'
  | 'reconnecting'
  | 'needs_pairing'
  | 'conflict';
export interface ConnectorHooks {
  allowed: (group: string) => boolean;
  incoming: (message: Incoming) => Promise<void>;
  state: (state: ConnectionState, qr?: string) => void;
  update: (group: string, id: string, patch: Partial<StoredMessage>) => void;
  typing: (group: string, active: boolean) => void;
  authGet: (key: string) => string | undefined;
  authSet: (key: string, value: string | null) => void;
  authBatch: (data: Record<string, string | null>) => void;
  getMessage: (group: string, id: string) => StoredMessage | undefined;
  maxMedia: number;
}
export interface Connector {
  readonly capabilities: Capabilities;
  start(): Promise<void>;
  stop(logout?: boolean): Promise<void>;
  groups(): Promise<ChatGroup[]>;
  send(message: Outgoing): Promise<Incoming>;
  react(group: string, message: StoredMessage, emoji: string): Promise<void>;
  edit(group: string, message: StoredMessage, text: string): Promise<void>;
  delete(group: string, message: StoredMessage): Promise<void>;
  presence(group: string, active: boolean): Promise<void>;
  read(group: string, messages: StoredMessage[]): Promise<void>;
  history(group: string, oldest: StoredMessage): Promise<void>;
  approved?(group: string): Promise<void>;
}
