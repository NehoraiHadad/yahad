export type ChildIdentity = { id: string; display?: string; username: string };
/** Labels are presentation only: selection and grants always use immutable IDs. */
export function childLabel(child: ChildIdentity, children: readonly ChildIdentity[]): string {
  const name = child.display?.trim() || child.username;
  const duplicate = children.some(other => other.id !== child.id && (other.display?.trim() || other.username) === name);
  return duplicate ? `${name} (${child.username})` : name;
}
