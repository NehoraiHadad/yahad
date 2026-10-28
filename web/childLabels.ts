export type ChildIdentity = { id: string; name: string };

export function childLabel(child: ChildIdentity) {
  return child.name;
}
