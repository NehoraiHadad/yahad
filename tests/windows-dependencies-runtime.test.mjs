import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, existsSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';

const prepared=process.env.YAHAD_PREPARED_WINDOWS_FULL;
test('verified private Windows media and Caddy metadata/config with empty PATH',
  {skip:process.platform!=='win32'||!prepared,timeout:60000}, async t=>{
    assert.equal(process.execPath.toLowerCase(),resolve(prepared,'runtime/node/node.exe').toLowerCase());
    const root=mkdtempSync(join(tmpdir(),'yahad-private-dependencies-'));
    const environment={SystemRoot:process.env.SystemRoot,WINDIR:process.env.SystemRoot,PATH:'',TEMP:root,TMP:root,
      APPDATA:join(root,'caddy-data'),LOCALAPPDATA:join(root,'local-data')};
    const ffmpeg=join(prepared,'runtime/ffmpeg/ffmpeg.exe'),caddy=join(prepared,'runtime/caddy/caddy.exe');
    const run=(exe,args,options={})=>execFileSync(exe,args,{env:environment,cwd:root,windowsHide:true,timeout:25000,maxBuffer:8*1024*1024,...options});
    await t.test('pinned FFmpeg build exposes LGPL shared libopus without GPL/nonfree',()=>{
      const version=run(ffmpeg,['-version'],{encoding:'utf8',stdio:['pipe','pipe','pipe']});
      assert.match(version,/n9\.0\.2-37-gae19868753-20261028/);
      const config=run(ffmpeg,['-buildconf'],{encoding:'utf8',stdio:['pipe','pipe','pipe']});
      assert.match(config,/--enable-shared/);assert.match(config,/--enable-libopus/);
      assert.doesNotMatch(config,/(?:^|\s)--enable-(?:gpl|nonfree)(?:\s|$)/);
      const license=run(ffmpeg,['-L'],{encoding:'utf8',stdio:['pipe','pipe','pipe']});
      assert.match(license,/GNU Lesser General Public License/i);
    });
    await t.test('actual app media functions encode Opus and JPEG using the private executable',async()=>{
      const previous={...process.env};process.env={...environment,FFMPEG_PATH:ffmpeg};
      try{
        const {voiceToOpus,thumbnail}=await import(pathToFileURL(join(prepared,'app/src/media.ts')));
        const frames=8000,wav=Buffer.alloc(44+frames*2);wav.write('RIFF');wav.writeUInt32LE(wav.length-8,4);wav.write('WAVEfmt ',8);
        wav.writeUInt32LE(16,16);wav.writeUInt16LE(1,20);wav.writeUInt16LE(1,22);wav.writeUInt32LE(16000,24);
        wav.writeUInt32LE(32000,28);wav.writeUInt16LE(2,32);wav.writeUInt16LE(16,34);wav.write('data',36);wav.writeUInt32LE(frames*2,40);
        for(let n=0;n<frames;n++)wav.writeInt16LE(Math.round(Math.sin(n*Math.PI*880/16000)*4000),44+n*2);
        const encoded=await voiceToOpus(wav);assert.equal(encoded.subarray(0,4).toString(),'OggS');assert.ok(encoded.includes(Buffer.from('OpusHead')));
        const decoded=run(ffmpeg,['-hide_banner','-loglevel','error','-nostdin','-protocol_whitelist','pipe,crypto,data','-i','pipe:0','-f','s16le','-ac','1','-ar','16000','pipe:1'],{input:encoded,stdio:['pipe','pipe','pipe']});
        assert.ok(decoded.length>=12000&&decoded.length<=18000,'Opus round-trip sample count');
        const ppm=Buffer.concat([Buffer.from('P6\n2 2\n255\n'),Buffer.from([255,0,0,0,255,0,0,0,255,255,255,255])]);
        const jpeg=await thumbnail(ppm);assert.equal(jpeg.readUInt16BE(0),0xffd8);assert.equal(jpeg.readUInt16BE(jpeg.length-2),0xffd9);
        const png=run(ffmpeg,['-hide_banner','-loglevel','error','-nostdin','-i','pipe:0','-frames:v','1','-c:v','png','-f','image2pipe','pipe:1'],{input:ppm,stdio:['pipe','pipe','pipe']});
        for(const image of [png,jpeg]){const preview=await thumbnail(image);assert.equal(preview.readUInt16BE(0),0xffd8);}
        for(const args of [['-c:a','aac','-f','mp4','-movflags','frag_keyframe+empty_moov'],['-c:a','libopus','-f','webm']]){
          const browserAudio=run(ffmpeg,['-hide_banner','-loglevel','error','-nostdin','-i','pipe:0',...args,'pipe:1'],{input:wav,stdio:['pipe','pipe','pipe']});
          const opus=await voiceToOpus(browserAudio);assert.ok(opus.includes(Buffer.from('OpusHead')));
        }

      }finally{process.env=previous;}
    });
    await t.test('Caddy adapts private-LAN plan without listeners, trust install or CA generation',()=>{
      assert.match(run(caddy,['version'],{encoding:'utf8'}),/^v2\.11\.7\s/);
      // Written by the pure plan generator; no --validate, run, start or trust command.
      const plan=process.env.YAHAD_CADDY_PLAN;
      assert.ok(plan,'explicit generated planning file required');
      const adapted=JSON.parse(run(caddy,['adapt','--config',plan,'--adapter','caddyfile'],{encoding:'utf8',stdio:['pipe','pipe','pipe']}));
      assert.equal(adapted.admin.disabled,true);
      const servers=Object.values(adapted.apps.http.servers);assert.equal(servers.length,1);
      assert.deepEqual(servers[0].listen,['192.168.4.5:443']);
      assert.equal(servers[0].automatic_https.disable_redirects,true);
      assert.equal(adapted.apps.pki.certificate_authorities.local.install_trust,false);
      assert.ok(JSON.stringify(adapted).includes('127.0.0.1:3000'));
      assert.equal(existsSync(environment.APPDATA),false);assert.equal(existsSync(environment.LOCALAPPDATA),false);
    });
  });
