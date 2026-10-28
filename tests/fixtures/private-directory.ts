import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {mkdtempSync,readFileSync,statSync,unlinkSync} from 'node:fs';
import {join,dirname} from 'node:path';
import {tmpdir} from 'node:os';
import {randomUUID} from 'node:crypto';

const system32=join(process.env.SystemRoot||'C:\\Windows','System32');
let sid:string|undefined;
function currentSid(){
  return sid ||= execFileSync(join(system32,'whoami.exe'),['/user','/fo','csv','/nh'],{encoding:'utf8',windowsHide:true}).match(/S-1-5-\d+(?:-\d+)+/)?.[0] || (()=>{throw new Error('fixture SID unavailable');})();
}
export function privateFixtureDirectory(prefix:string){
  const directory=mkdtempSync(join(tmpdir(),prefix));
  if(process.platform==='win32')execFileSync(join(system32,'icacls.exe'),[directory,'/inheritance:r','/grant:r',`*${currentSid()}:(OI)(CI)F`,'*S-1-5-18:(OI)(CI)F','*S-1-5-32-544:(OI)(CI)F'],{windowsHide:true,stdio:'pipe'});
  return directory;
}
export function assertPrivateFixtureFile(path:string){
  if(process.platform!=='win32'){assert.equal(statSync(path).mode&0o777,0o600);return;}
  // Inspect actual DACLs, including intentional queue/wake hardlinks. The secret
  // reader's stricter single-link policy is preserved and is not repurposed here.
  const snapshot=join(dirname(path),'.fixture-acl-'+randomUUID());
  try{
    execFileSync(join(system32,'icacls.exe'),[path,'/save',snapshot],{windowsHide:true,stdio:'pipe'});
    const lines=readFileSync(snapshot,'utf16le').replace(/^\uFEFF/,'').trim().split(/\r?\n/);
    const sddl=lines.find(line=>line.startsWith('D:'));assert.ok(sddl,'DACL snapshot missing');
    const aces=sddl.match(/\([^()]*\)/g);assert.ok(aces?.length,'empty/null DACL');
    const allowed=new Set([currentSid(),'SY','BA','S-1-5-18','S-1-5-32-544']);
    for(const ace of aces){const fields=ace.slice(1,-1).split(';');assert.equal(fields.length,6);if(fields[0]==='D')continue;assert.equal(fields[0],'A','unknown fixture ACE shape');assert.ok(allowed.has(fields[5]),'fixture grants an unrelated principal');}
  }finally{try{unlinkSync(snapshot);}catch{}}
}
