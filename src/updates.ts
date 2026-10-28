import { readFile, open, lstat, link, unlink } from 'node:fs/promises';
import { join } from 'node:path';
import { randomUUID } from 'node:crypto';
import { z } from 'zod';
import type { Config } from './config.js';
import type { User } from './store.js';

const updateError = z.enum(['configuration','network','signature','archive','build','backup','health','rollback','storage','incompatible','internal']);
const statusSchema = z.object({
  enabled: z.boolean(), mode: z.enum(['notify', 'auto']),
  modeControlAllowed: z.boolean().default(false),
  lastModeRequest: z.object({id:z.uuid(),mode:z.enum(['notify','auto']),state:z.enum(['pending','applied','rejected']),
    error:z.enum(['configuration','expired','replayed','local_setup','operator_changed','busy']).nullable()}).nullable().optional(),
  lastRequest: z.object({id:z.uuid(),action:z.enum(['check','install']),state:z.enum(['running','succeeded','failed']),
    startedAt:z.number(),finishedAt:z.number().nullable(),error:updateError.or(z.literal('expired')).nullable()}).nullable().optional(),
  operator: z.object({ familyId: z.string().max(100), userId: z.string().max(100) }),
  phase: z.enum(['idle','available','checking','preparing','downloading','verifying','building','stopping','backing_up','activating','checking_health','healthy','rolled_back','failed','incompatible','rolling_back','recovery_required','rollback_pending_health']),
  currentVersion: z.string().max(40).nullable(), latestVersion: z.string().max(40).nullable(),
  checkedAt: z.number().nullable(), updatedAt: z.number(),
  error: updateError.nullable(),
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
    let queuedRequest: {id:string;action:'check'|'install'|'set_mode';mode?:'notify'|'auto';createdAt:number} | null = null;
    try {
      const requestPath=join(config.dataDir,'update-request.json'), queuedStat=await lstat(requestPath);
      if(queuedStat.isFile()&&!queuedStat.isSymbolicLink()&&queuedStat.size<=512){
        const request=JSON.parse(await readFile(requestPath,'utf8'));
        if(request.familyId===user.familyId&&request.userId===user.id&&z.uuid().safeParse(request.id).success&&
          ['check','install','set_mode'].includes(request.action)&&Number.isFinite(request.createdAt)&&
          (request.action!=='set_mode'||['auto','notify'].includes(request.mode))){
          queuedRequest={id:request.id,action:request.action,createdAt:request.createdAt,...request.action==='set_mode'?{mode:request.mode}:{}};
          if(request.action==='set_mode')queuedMode={id:request.id,mode:request.mode};
        }
      }
    } catch {}
    return { configured: true as const, ...visible, queuedMode, queuedRequest, stale: Date.now() - status.updatedAt > 86400000 };
  } catch { return { configured: false as const }; }
}
export async function requestUpdate(config: Config, user: User, action: 'check' | 'install' | 'set_mode', mode?: 'notify'|'auto') {
  const status = await updateStatus(config, user);
  if (!status.configured || !status.enabled || status.stale) return 'unavailable';
  // Neither a queued install nor a mode/check request can repair these states.
  // In automatic mode even "check" may install, so reject every POST here;
  // GET status remains readable; only the root worker's recovery path clears it.
  if (status.phase === 'recovery_required' || status.phase === 'rollback_pending_health') return 'unavailable';
  if(action==='set_mode'&&(!status.modeControlAllowed||!mode))return 'mode_locked';
  if(status.lastModeRequest?.state==='pending')return 'pending';
  if(status.lastRequest?.state==='running')return 'pending';
  const id = randomUUID();
  const temporary=join(config.dataDir, '.update-request-'+id+'.tmp');
  let created=false;
  try {
    // Publish only complete JSON: a path-activated worker may wake on creation,
    // before writeFile to the public queue filename would have finished. link()
    // atomically publishes without replacing an existing queued request.
    const file=await open(temporary,'wx',0o600);created=true;
    try {
      await file.writeFile(JSON.stringify({ id, action, ...(action==='set_mode'?{mode}:{}),
        familyId: user.familyId, userId: user.id, createdAt: Date.now() }));
      await file.sync();
    } finally { await file.close(); }
    await link(temporary, join(config.dataDir, 'update-request.json'));
    // Level-triggered latch closes the path service's shutdown/re-arm race.
    // It is only a wake signal; root still authenticates the separate queue.
    // Once published, the request may already execute. A failed optional wake
    // must not report failure to the caller and invite a duplicate submission;
    // PathChanged and the timer still consume the authoritative queue.
    await link(temporary,join(config.dataDir,'update-request.wake')).catch(()=>{});
    return 'queued';
  } catch (e) { if ((e as NodeJS.ErrnoException).code === 'EEXIST') return 'pending'; throw e; }
  finally { if(created)await unlink(temporary).catch(()=>{}); }
}
