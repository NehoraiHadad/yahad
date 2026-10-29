export type Kind = 'text' | 'image' | 'video' | 'document' | 'sticker' | 'audio' | 'unsupported';
export type Receipt = 'pending' | 'sent' | 'delivered' | 'read' | 'unknown' | 'failed';
export interface ReactionUpdate { target: string; actor: string; emoji: string; time?: number }
export interface Capabilities {
  polls?: { identify: boolean; create: boolean; vote: boolean; results: boolean };
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
  kind?: 'group' | 'direct';
  lastMessageTime?: number;
  id: string;
  title: string;
}
export interface Incoming {
  restricted?: boolean;
  guard?: () => boolean;
  history?: { requestId?: string };
  id: string;
  groupId: string;
  sender: string;
  fromMe: boolean;
  time: number;
  kind: Kind;
  text: string;
  unsupportedReason?: 'poll' | 'poll-vote' | 'poll-results';
  replyId?: string;
  mime?: string;
  filename?: string;
  raw?: string;
  bytes?: Buffer;
  download?: () => Promise<Buffer>;
  status?: Receipt;
  edited?: boolean;
  editTime?: number;
  deleted?: boolean;
  reaction?: ReactionUpdate;
  historicalReactions?: ReactionUpdate[];
}
export interface StoredMessage {
  restricted?: boolean;
  id: string;
  groupId: string;
  sender: string;
  fromMe: boolean;
  time: number;
  kind: Kind;
  text: string;
  unsupportedReason?: 'poll' | 'poll-vote' | 'poll-results';
  replyId?: string;
  mediaId?: string;
  mime?: string;
  filename?: string;
  status: Receipt;
  edited?: boolean;
  editTime?: number;
  originalTimeKnown?: boolean;
  deleted?: boolean;
  reactions: Record<string, string>;
  reactionTimes?: Record<string, number>;
  raw?: string;
  clientId?: string;
  authorUserId?: string;
  authorLabel?: string;
}
export interface Outgoing {
  /** Recheck the originating session after preparation, immediately before provider submission. */
  beforeSend?: () => void;
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
  /** Only authenticated provider events may discover direct conversations. */
  discoverDirect?: (chats: ChatGroup[]) => void;
  discoverGroups?: (chats: ChatGroup[]) => void;
  historyEmpty?: (requestId: string) => void;
  retentionToken?: (group: string) => string | undefined;
  history?: (notice: HistoryNotice) => void;
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
  originalAvailable?(message: StoredMessage): boolean;
  readonly capabilities: Capabilities;
  start(): Promise<void>;
  stop(logout?: boolean): Promise<void | { remoteRevoked: boolean; unlinkRequested?: boolean }>;
  groups(): Promise<ChatGroup[]>;
  groupPhoto?(group: string): Promise<import("../group-photos.js").GroupPhoto | undefined>;
  send(message: Outgoing): Promise<Incoming>;
  react(group: string, message: StoredMessage, emoji: string): Promise<void>;
  edit(group: string, message: StoredMessage, text: string): Promise<void>;
  delete(group: string, message: StoredMessage): Promise<void>;
  presence(group: string, active: boolean): Promise<void>;
  read(group: string, messages: StoredMessage[]): Promise<void>;
  history(group: string, oldest: StoredMessage): Promise<string | void>;
  historyAnchor?(group: string, message: StoredMessage): boolean;
  approved?(group: string): Promise<void>;
}
export interface HistoryNotice {
  batchId?: string;
  admitted?: number;
  finished?: boolean;
  final?: boolean;
  groupId: string;
  phase: 'start' | 'end' | 'overload' | 'error';
  received: number;
  processed: number;
  history: boolean;
  requestId?: string;
}
