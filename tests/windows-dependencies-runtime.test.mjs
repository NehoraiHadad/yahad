import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, existsSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';

const prepared=process.env.YAHAD_PREPARED_WINDOWS_FULL;
test('verified private Windows Caddy adapts the private-LAN plan with empty PATH, without listeners, trust install or CA generation',
  {skip:process.platform!=='win32'||!prepared,timeout:60000}, ()=>{
    assert.equal(process.execPath.toLowerCase(),resolve(prepared,'runtime/node/node.exe').toLowerCase());
    const root=mkdtempSync(join(tmpdir(),'yahad-private-dependencies-'));
    const environment={SystemRoot:process.env.SystemRoot,WINDIR:process.env.SystemRoot,PATH:'',TEMP:root,TMP:root,
      APPDATA:join(root,'caddy-data'),LOCALAPPDATA:join(root,'local-data')};
    const caddy=join(prepared,'runtime/caddy/caddy.exe');
    const run=(args,options={})=>execFileSync(caddy,args,{env:environment,cwd:root,windowsHide:true,timeout:25000,maxBuffer:8*1024*1024,...options});
    assert.match(run(['version'],{encoding:'utf8'}),/^v2\.11\.7\s/);
    // Written by the pure plan generator; no --validate, run, start or trust command.
    const plan=process.env.YAHAD_CADDY_PLAN;
    assert.ok(plan,'explicit generated planning file required');
    const adapted=JSON.parse(run(['adapt','--config',plan,'--adapter','caddyfile'],{encoding:'utf8',stdio:['pipe','pipe','pipe']}));
    assert.equal(adapted.admin.disabled,true);
    const servers=Object.values(adapted.apps.http.servers);assert.equal(servers.length,1);
    assert.deepEqual(servers[0].listen,['192.168.4.5:443']);
    assert.equal(servers[0].automatic_https.disable_redirects,true);
    assert.equal(adapted.apps.pki.certificate_authorities.local.install_trust,false);
    assert.ok(JSON.stringify(adapted).includes('127.0.0.1:3000'));
    assert.equal(existsSync(environment.APPDATA),false);assert.equal(existsSync(environment.LOCALAPPDATA),false);
  });
