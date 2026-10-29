"""Build a small native downloader. Never publishes or invokes installation."""
from pathlib import Path
import argparse,json,hashlib,subprocess,urllib.parse,os
from verify_pe import inspect
parser=argparse.ArgumentParser();parser.add_argument('--zig',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);parser.add_argument('--payload-url');args=parser.parse_args()
root=Path(__file__).resolve().parent;output=args.output.resolve();output.mkdir(parents=True,exist_ok=True)
pin=json.loads((root/'payload-pin.json').read_text(encoding='utf-8'));url=args.payload_url or ''
if url:
    p=urllib.parse.urlsplit(url)
    if len(url)>4095 or p.scheme!='https' or not p.hostname or p.port not in (None,443) or p.username is not None or p.password is not None or p.fragment or any(c in url for c in '\r\n\t\\'):raise ValueError('A fixed HTTPS distribution URL without credentials or fragment is required')
header='#pragma once\nstatic constexpr unsigned long long PAYLOAD_BYTES='+str(pin['bytes'])+'ULL;\nstatic constexpr const char PAYLOAD_SHA[]='+json.dumps(pin['sha256'])+';\n'
(output/'payload_pin.hpp').write_text(header,encoding='utf-8')
env=os.environ.copy();env['ZIG_GLOBAL_CACHE_DIR']=str(output/'zig-cache');env['ZIG_LOCAL_CACHE_DIR']=str(output/'zig-cache-local')
common=[str(args.zig.resolve()),'c++','-target','x86_64-windows-gnu','-std=c++17','-Os','-static','-s','-municode','-DUNICODE','-D_UNICODE',str(root/'loader.cpp'),'-I',str(output),'-I',str(root)]
libs=['-lwinhttp','-lbcrypt','-lshell32','-lole32','-ladvapi32','-lcomctl32','-lgdi32','-luser32','-luuid']
def run(command):subprocess.run(command,cwd=root,env=env,check=True)
(output/'payload-url.bin').write_bytes((url+'\0').encode('utf-16-le'))
notices='Yahad native bootstrap\nStatic native support libraries: LLVM libc++/libc++abi/libunwind (LLVM 20.1.2, Apache-2.0 with LLVM exceptions) and MinGW-w64 CRT.\nThe compiler is not distributed with this installer.\n\n'
for name in ['LLVM-LICENSE.txt','MINGW-COPYING.txt']:notices+='\n'+name+'\n'+(root/name).read_text(encoding='utf-8')
(output/'third-party-notices.txt').write_text(notices,encoding='utf-8')
rc=output/'bootstrap.generated.rc';rc.write_text('1 24 "'+(root/'bootstrap.manifest').as_posix()+'"\n102 RCDATA "'+(output/'payload-url.bin').as_posix()+'"\n103 RCDATA "'+(output/'third-party-notices.txt').as_posix()+'"\n',encoding='utf-8')
resource=output/'bootstrap.res';run([str(args.zig.resolve()),'rc','/fo',str(resource),str(rc)])
run(common+['-mwindows',str(root/'main.cpp'),str(resource),'-o',str(output/'Yahad.Setup.Web.exe')]+libs)
run(common+[str(root/'tests.cpp'),'-o',str(output/'Yahad.Bootstrap.Tests.exe')]+libs)
run(common+['-mwindows',str(root/'child_probe.cpp'),str(resource),'-o',str(output/'Yahad.Harmless.Child.exe')]+libs)
run(common+[str(root/'http_probe.cpp'),'-o',str(output/'Yahad.Http.Probe.exe')]+libs)
exe=output/'Yahad.Setup.Web.exe';pin.update(url=url or None,distributionConfigured=bool(url));result={'launcherBytes':exe.stat().st_size,'launcherSha256':hashlib.file_digest(exe.open('rb'),'sha256').hexdigest(),'payload':pin,'nativeImage':inspect(exe,pin['sha256']),'hostInstallExecuted':False,'compiler':subprocess.check_output([str(args.zig.resolve()),'version'],env=env,text=True).strip()}
(output/'build-evidence.json').write_text(json.dumps(result,indent=2),encoding='utf-8');print(json.dumps(result))
