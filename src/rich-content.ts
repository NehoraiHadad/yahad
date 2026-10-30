import type {Kind} from './connectors/types.js';

export function readOnlyKind(kind:Kind):boolean {
  return kind==='poll' || kind==='contact' || kind==='location' || kind==='interactive';
}
export const sharedTextLimit=16_384;
export function sharedText(value:unknown):string|undefined {
  return typeof value==='string' && value.trim() && value.length<=sharedTextLimit ? value : undefined;
}
