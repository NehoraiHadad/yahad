#!/usr/bin/env python3
"""Opt-in real systemd mount-sandbox check. Temporary fixtures/transient units only.
Never touches Yachad service/config/data; requires isolated Linux root systemd.
"""
import json, pathlib, subprocess, tempfile, uuid, sys

def trial(root,writable):
    managed=root/'etc/yachad';managed.mkdir(parents=True)
    config=managed/'updates.json';config.write_text('{}')
    protected=[managed/'data.key',managed/'yachad.env',managed/'backup-secret']
    for path in protected:path.write_bytes(b'synthetic-fixture-unchanged');path.chmod(0o600)
    script=root/'check.py'
    script.write_text('''import pathlib,os,sys,json
folder=pathlib.Path(sys.argv[1]);writable=sys.argv[2]=='yes'
try:
 p=folder/'updates.json.tmp';p.write_text('{"mode":"notify"}');p.replace(folder/'updates.json')
 changed=True
except OSError:changed=False
assert changed==writable
for name in ['data.key','yachad.env','backup-secret']:
 p=folder/name
 try:p.write_bytes(b'changed')
 except OSError:pass
 else:raise AssertionError('protected file writable')
 try:p.rename(folder/(name+'.moved'))
 except OSError:pass
 else:raise AssertionError('protected file replaceable')
 assert p.read_bytes()==b'synthetic-fixture-unchanged'
print('SANDBOX_FIXED_OK' if writable else 'SANDBOX_BASELINE_WRITE_BLOCKED')
''')
    args=['systemd-run','--quiet','--wait','--collect','--pipe','--unit=yachad-fixture-'+uuid.uuid4().hex,
          '-p','ProtectSystem=strict','-p','PrivateNetwork=yes','-p','NoNewPrivileges=yes']
    if writable:args+=['-p','ReadWritePaths='+str(managed)]
    args+=['-p','ReadOnlyPaths='+' '.join(str(p) for p in protected),'--','/usr/bin/python3',str(script),str(managed),'yes' if writable else 'no']
    result=subprocess.run(args,stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=30,text=True)
    if result.returncode:
        print('SYSTEMD_SANDBOX_BLOCKED',result.returncode,result.stderr.strip()[:500]);return False
    print(result.stdout.strip());return True
with tempfile.TemporaryDirectory(prefix='yachad-sandbox-') as temp:
    root=pathlib.Path(temp);root.chmod(0o755)
    if not trial(root/'baseline',False) or not trial(root/'fixed',True):sys.exit(1)
print('SYSTEMD_SANDBOX_OK: config atomic rename succeeds only with override; key/env/secret writes and rename remain blocked')
