import { readFile, writeFile, lstat } from 'node:fs/promises';
import { join } from 'node:path';
import { randomUUID } from 'node:crypto';
import { z } from 'zod';
import type { Config } from './config.js';
import type { User } from './store.js';

const statusSchema = z.object({
  enabled: z.boolean(), mode: z.enum(['notify', 'auto']),
  modeControlAllowed: z.boolean().default(false),
  lastModeRequest: z.object({id:z.uuid(),mode:z.enum(['notify','auto']),state:z.enum(['pending','applied','rejected']),
    error:z.enum(['configuration','expired','replayed','local_setup','operator_changed','busy']).nullable()}).nullable().optional(),
  operator: z.object({ familyId: z.string().max(100), userId: z.string().max(100) }),
  phase: z.enum(['idle','available','preparing','stopping','backing_up','activating','checking_health','healthy','rolled_back','failed','incompatible','rolling_back','recovery_required','rollback_pending_health']),
  currentVersion: z.string().max(40).nullable(), latestVersion: z.string().max(40).nullable(),
  checkedAt: z.number().nullable(), updatedAt: z.number(),
  error: z.enum(['configuration','network','signature','archive','build','backup','health','rollback','storage','incompatible','internal']).nullable(),
});
export async function updateStatus(config: Config, user: User) {
  try {
    const path = config.updateStatusFile || '/var/lib/yachad-updates/status.json';
    const st = await lstat(path);
    if (!st.isFile() || st.isSymbolicLink() || st.size > 8192) throw new Error('Invalid status');
    const status = statusSchema.parse(JSON.parse(await readFile(path, 'utf8')));
    const operator = user.role === 'parent' && status.operator.familyId === user.familyId && status.operator.userId === user.id;
    if (!operator) return { configured: false as const };
    const { operator: _operator, ...visible } = status;
    let queuedMode: {id:string;mode:'notify'|'auto'} | null = null;
    try {
      const requestPath=join(config.dataDir,'update-request.json'), queuedStat=await lstat(requestPath);
      if(queuedStat.isFile()&&!queuedStat.isSymbolicLink()&&queuedStat.size<=512){
        const request=JSON.parse(await readFile(requestPath,'utf8'));
        if(request.action==='set_mode'&&request.familyId===user.familyId&&request.userId===user.id&&z.uuid().safeParse(request.id).success&&['auto','notify'].includes(request.mode))
          queuedMode={id:request.id,mode:request.mode};
      }
    } catch {}
    return { configured: true as const, ...visible, queuedMode, stale: Date.now() - status.updatedAt > 86400000 };
  } catch { return { configured: false as const }; }
}
export async function requestUpdate(config: Config, user: User, action: 'check' | 'install' | 'set_mode', mode?: 'notify'|'auto') {
  const status = await updateStatus(config, user);
  if (!status.configured || !status.enabled || status.stale) return 'unavailable';
  if(action==='set_mode'&&(!status.modeControlAllowed||!mode))return 'mode_locked';
  if(status.lastModeRequest?.state==='pending')return 'pending';
  const id = randomUUID();
  try {
    await writeFile(join(config.dataDir, 'update-request.json'), JSON.stringify({ id, action, ...(action==='set_mode'?{mode}:{}),
      familyId: user.familyId, userId: user.id, createdAt: Date.now() }), { mode: 0o600, flag: 'wx' });
    return 'queued';
  } catch (e) { if ((e as NodeJS.ErrnoException).code === 'EEXIST') return 'pending'; throw e; }
}
