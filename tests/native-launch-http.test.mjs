import {test} from 'node:test';
import assert from 'node:assert/strict';
import {spawn,execFileSync} from 'node:child_process';
import {mkdtempSync,mkdirSync,writeFileSync,readFileSync,existsSync} from 'node:fs';
import {join,resolve} from 'node:path';
import {tmpdir} from 'node:os';
import {randomBytes,randomUUID} from 'node:crypto';
import {pathToFileURL} from 'node:url';
import {createServer} from 'node:net';
import {once} from 'node:events';

const payload=process.env.YAHAD_NATIVE_LAUNCH_PAYLOAD,host=process.env.YAHAD_NATIVE_FIXTURE_HOST,manifest=process.env.YAHAD_NATIVE_LAUNCH_MANIFEST;
test('native launcher owns private FFmpeg environment and serves actual mock-app voice/image HTTP requests',
 {skip:process.platform!=='win32'||!payload||!host||!manifest,timeout:90000},async()=>{
  const root=mkdtempSync(join(tmpdir(),'yahad-native-launch-'));const system32=join(process.env.SystemRoot,'System32');
  const sid=execFileSync(join(system32,'whoami.exe'),['/user','/fo','csv','/nh'],{encoding:'utf8',windowsHide:true}).match(/S-1-5-\d+(?:-\d+)+/)?.[0];assert.ok(sid);
  execFileSync(join(system32,'icacls.exe'),[root,'/inheritance:r','/grant:r',`*${sid}:(OI)(CI)F`,'*S-1-5-18:(OI)(CI)F','*S-1-5-32-544:(OI)(CI)F'],{windowsHide:true,stdio:'pipe'});
  const data=join(root,'data'),cache=join(root,'cache');mkdirSync(data);mkdirSync(cache);
  const key=randomBytes(32),keyPath=join(root,'master-key');writeFileSync(keyPath,key.toString('base64'));
  const server=createServer();server.listen(0,'127.0.0.1');await once(server,'listening');const port=server.address().port;await new Promise(done=>server.close(done));
  const origin=`http://127.0.0.1:${port}`,group='120363001@g.us';
  const {Store}=await import(pathToFileURL(join(payload,'app/src/store.ts')));
  const config={dataDir:data,masterKey:key,connector:'mock',realAllowed:false,host:'127.0.0.1',port,origin,secureCookie:false,maxMedia:1024*1024,maxStorage:64*1024*1024,childIdleMs:900000,sessionMs:3600000};
  const store=new Store(config);const family=await store.createFamily('Synthetic fixture',{name:'Fixture Parent',password:'fixture-parent-password'},{name:'Fixture Child',password:'fixture-child-password'});
  store.syncGroups(family.familyId,[{id:group,title:'Synthetic fixture group'}]);store.grant(family.familyId,family.childId,group,true);store.run('UPDATE families SET linked=1 WHERE id=?',family.familyId);
  const user=store.user(store.one('SELECT * FROM users WHERE family_id=? AND id=?',family.familyId,family.childId));const session=store.createSession(user);store.close();
  const settings=join(root,'settings.json');writeFileSync(settings,JSON.stringify({TemporaryDirectory:cache,DataDirectory:data,MasterKeyPath:keyPath,Port:port}));
  const marker=join(root,'inherited-node-hook'),hook=join(root,'host-hook.mjs');writeFileSync(hook,`import{writeFileSync}from'node:fs';writeFileSync(${JSON.stringify(marker)},'BAD');process.exit(29);`);
  const environment={...process.env,NODE_OPTIONS:'--import '+pathToFileURL(hook).href,NODE_PATH:root,ESBUILD_BINARY_PATH:join(root,'untrusted-esbuild.exe'),FFMPEG_PATH:join(root,'wrong-global-ffmpeg.exe'),OPENSSL_CONF:join(root,'wrong-openssl.conf'),PATH:''};
  // The fixture host's own CLR is already self-contained; do not load developer hooks into it.
  for(const key of Object.keys(environment))if(/^(?:DOTNET_|CORECLR_|COR_|COMPLUS_)/i.test(key))delete environment[key];
  const child=spawn(host,['--fixture-app',payload,manifest,settings],{env:environment,windowsHide:true,stdio:['pipe','pipe','pipe']});const exited=once(child,'exit');let output='',errors='';
  child.stdout.on('data',data=>output+=data);child.stderr.on('data',data=>errors+=data);
  try{
    const until=Date.now()+45000;while(!output.includes('"ready":true')){assert.equal(child.exitCode,null,`native host failed: ${errors}`);assert.ok(Date.now()<until,'native host startup deadline');await new Promise(done=>setTimeout(done,40));}
    assert.equal(existsSync(marker),false,'host NODE_OPTIONS reached private Node');
    const health=await fetch(origin+'/health/ready');assert.equal(health.status,200);
    const settingsState=await fetch(origin+'/api/setup/status').then(r=>r.json());assert.equal(settingsState.state,'complete','existing fixture family must receive login');
    const frames=8000,wav=Buffer.alloc(44+frames*2);wav.write('RIFF');wav.writeUInt32LE(wav.length-8,4);wav.write('WAVEfmt ',8);wav.writeUInt32LE(16,16);wav.writeUInt16LE(1,20);wav.writeUInt16LE(1,22);wav.writeUInt32LE(16000,24);wav.writeUInt32LE(32000,28);wav.writeUInt16LE(2,32);wav.writeUInt16LE(16,34);wav.write('data',36);wav.writeUInt32LE(frames*2,40);
    for(let n=0;n<frames;n++)wav.writeInt16LE(Math.round(Math.sin(n*Math.PI*880/16000)*4000),44+n*2);
    async function upload(bytes,mime,name,voice){const form=new FormData();form.append('file',new Blob([bytes],{type:mime}),name);form.append('clientId',randomUUID());if(voice)form.append('voice','true');const response=await fetch(origin+'/api/groups/'+encodeURIComponent(group)+'/media',{method:'POST',headers:{origin,cookie:'sid='+session.sid,'x-csrf-token':session.csrf},body:form});const body=await response.json();assert.equal(response.status,200,JSON.stringify(body));return body.message;}
    const audio=await upload(wav,'audio/wav','fixture.wav',true);assert.equal(audio.kind,'audio');assert.match(audio.mime,/audio\/ogg/);
    const image=await upload(readFileSync(join(payload,'app/fixtures/garden.jpg')),'image/jpeg','fixture.jpg',false);assert.equal(image.kind,'image');
    assert.equal(existsSync(marker),false);
    child.stdin.end('stop\n');assert.equal((await exited)[0],0,errors);assert.match(output,/"stopped":true/);
    const {createPlatformFiles}=await import(pathToFileURL(join(payload,'app/src/platform-files.ts')));await(await createPlatformFiles('win32',join(payload,'app/native/Yahad.Platform.exe')).acquireDataLock(data))();
    assert.equal(readFileSync(keyPath,'utf8'),key.toString('base64'),'existing fixture key changed');
  }finally{if(child.exitCode===null&&child.signalCode===null){child.kill();await exited;}}
 });
