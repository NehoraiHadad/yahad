/** One account name is used both for display and login. IDs remain the durable identity. */
export function normalizeAccountName(value: string): string {
  const name = value.normalize('NFC').replace(/^ +| +$/g, '').replace(/ +/g, ' ');
  if (!name || Array.from(name).length > 60 || !/^[\p{L}\p{M}\p{N} ._-]+$/u.test(name) || !/[\p{L}\p{N}]/u.test(name))
    throw new Error('Invalid account name');
  return name;
}
/** Internal uniqueness/login index, never an alternative user-facing name. */
export function accountNameKey(name: string): string {
  return normalizeAccountName(name).toLowerCase().normalize('NFC');
}
