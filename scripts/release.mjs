// Offline release packaging. Never publishes or connects to GitHub.
import { generateKeyPairSync, createHash, sign } from 'node:crypto';
import { readFileSync, writeFileSync, mkdirSync, existsSync, statSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { execFileSync } from 'node:child_process';
const [command, output, keyFile] = process.argv.slice(2);
if (command === 'keygen') {
  if (!output || existsSync(output) || existsSync(output + '.pub')) throw new Error('Choose a new private key path');
  const keys = generateKeyPairSync('ed25519', { publicKeyEncoding: {type:'spki',format:'pem'}, privateKeyEncoding:{type:'pkcs8',format:'pem'} });
  writeFileSync(output, keys.privateKey, {mode:0o600,flag:'wx'});
  writeFileSync(output + '.pub', keys.publicKey, {mode:0o644,flag:'wx'});
  console.log('Signing key created locally. Keep the private PEM off the Pi and out of Git.');
} else if (command === 'package' || command === 'package-https') {
  const signed = command === 'package';
  if (!output || (signed && !keyFile) || (!signed && keyFile) || !existsSync('dist/index.html')) throw new Error('Build first; provide output directory and a private PEM only for signed packaging');
  if (signed && statSync(keyFile).mode & 0o077) throw new Error('Signing key must be private (0600)');
  const version = JSON.parse(readFileSync('package.json','utf8')).version;
  if (!/^\d+\.\d+\.\d+$/.test(version)) throw new Error('Stable semantic version required');
  const dir=resolve(output); mkdirSync(dir,{recursive:true,mode:0o700});
  const archive=join(dir,'yachad-app.zip');
  if (existsSync(archive)) throw new Error('Use an empty release output directory');
  execFileSync('python3',['-c',`
import pathlib,zipfile,sys
root=pathlib.Path.cwd()
files=['package.json','package-lock.json','README.md','LICENSE','tsconfig.json','vite.config.ts','.env.example']
for folder in ['src','scripts','web','tests','fixtures','deploy','docs','dist']:
 p=root/folder
 if p.exists(): files.extend(str(x.relative_to(root)) for x in p.rglob('*') if x.is_file() and '__pycache__' not in x.parts and x.suffix != '.pyc')
with zipfile.ZipFile(sys.argv[1],'x',compression=zipfile.ZIP_DEFLATED) as out:
 for name in sorted(set(files)):
  p=root/name
  if p.is_symlink() or not p.is_file(): raise ValueError('Only regular release files allowed')
  out.write(p,name)
`,archive],{stdio:['ignore','ignore','ignore']});
  const data=readFileSync(archive);
  const manifest=Buffer.from(JSON.stringify({format:1,version,databaseSchema:1,archive:'yachad-app.zip',bytes:data.length,sha256:createHash('sha256').update(data).digest('hex')})+'\n');
  writeFileSync(join(dir,'yachad-release.json'),manifest,{mode:0o600,flag:'wx'});
  if (signed) writeFileSync(join(dir,'yachad-release.sig'),sign(null,manifest,readFileSync(keyFile)),{mode:0o600,flag:'wx'});
  console.log(`Release ${version} packaged locally (${signed?'signed; three assets':'GitHub HTTPS trust; two assets'}). Upload to matching stable v${version} only after approval.`);
} else throw new Error('Use: node scripts/release.mjs package-https OUT_DIR | package OUT_DIR PRIVATE_KEY | keygen PRIVATE_KEY');
