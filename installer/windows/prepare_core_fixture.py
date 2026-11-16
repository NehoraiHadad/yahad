"""The app tree of one release, staged from a clean checkout (plan 4.1, 5.2).

Writes OUTPUT/app/: package.json, the built dist/, fixtures/, the public LAN help
(deploy/), one self-contained server bundle (app/server/main.mjs) and the app's
own legal records in app/legal/: what the server build writes (server-bundle.json,
ThirdPartyNotices-server.txt), the app SBOM (the server bundle's npm packages),
build-inputs.json and the Yahad license. Nothing of the infrastructure (Node,
Caddy, .NET, Platform) is staged here; that is infra_package.py.

Supply private tooling extracted from checksum-verified official Node archives.
npm and the bundler run here on the build machine only, against a lockfile-
restored private build root, so the family's machine has no node_modules,
TypeScript loader or npm install.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from .application_version import source_release_version
from .database_schema import source_schema


def stage_public_lan_help(repo, app):
    """Only these two public source assets belong in the app tree."""
    repo, app = Path(repo), Path(app)
    names = ('lan-help.html', 'lan-capabilities.json')
    sources = [repo/'deploy'/name for name in names]
    if any(not path.is_file() or path.is_symlink() or path.stat().st_size > 65536 for path in sources):
        raise ValueError('Complete public LAN help assets required')
    marker = json.loads(sources[1].read_text(encoding='utf-8'))
    expected = dict(format=1, localHttps=True, exactAdditionalOrigins=True,
                    ownerLoopbackBootstrap=True, publicHelpTemplate='deploy/lan-help.html')
    if marker != expected or type(marker.get('format')) is not int or any(marker.get(name) is not True for name in
            ('localHttps', 'exactAdditionalOrigins', 'ownerLoopbackBootstrap')):
        raise ValueError('Unsupported LAN help contract')
    target = app/'deploy'; target.mkdir()
    for path in sources: shutil.copyfile(path, target/path.name)
    return ['deploy/'+name for name in names]


def app_sbom(version, source_commit, lock, bundled):
    """Every npm package with code in the server bundle, with its registry identity."""
    return {'format':'yahad-app-inventory', 'schema':1, 'appVersion':version, 'sourceCommit':source_commit,
        'serverBundleDependencies':[{'path':item['path'], 'version':lock[item['path']]['version'], 'license':lock[item['path']].get('license'),
            'integrity':lock[item['path']].get('integrity')} for item in bundled],
        'notices':'app/legal/ThirdPartyNotices-server.txt'}


def prepare(repo, output, node, cache):
    repo, output, node, cache = (Path(p).resolve() for p in (repo, output, node, cache))
    # A clean tracked source checkpoint makes the recorded source commit useful.
    if subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=no'], cwd=repo, text=True).strip():
        raise ValueError('Commit tracked source before staging the app')
    source_commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip()
    version = source_release_version(repo)
    if output.exists() or output == repo or not cache.is_dir(): raise ValueError('New output and an existing offline cache required')
    output.mkdir(parents=True, exist_ok=False)
    app = output/'app'; app.mkdir()
    shutil.copyfile(repo/'package.json', app/'package.json')
    for name in ('dist', 'fixtures'): shutil.copytree(repo/name, app/name)
    stage_public_lan_help(repo, app)
    environment = {k:v for k,v in os.environ.items() if not k.upper().startswith(('NPM_CONFIG_', 'NODE_', 'DOTNET_', 'CORECLR_', 'COR_', 'ESBUILD_'))}
    environment.update(PATH=str(node)+os.pathsep+str(Path(os.environ['SystemRoot'])/'System32'),
        npm_config_cache=str(cache), npm_config_userconfig=str(output/'no-user-npm-config'),
        npm_config_globalconfig=str(output/'no-global-npm-config'))
    # The locked tree (the bundler is a devDependency) is restored into a private
    # build root beside the output; only the bundled server and its legal record leave it.
    with tempfile.TemporaryDirectory(prefix='yahad-server-build-', dir=output.parent) as build_root:
        build_root = Path(build_root)
        for name in ('package.json', 'package-lock.json', 'tsconfig.json'): shutil.copyfile(repo/name, build_root/name)
        shutil.copytree(repo/'src', build_root/'src')
        completed = subprocess.run([str(node/'node.exe'), str(node/'node_modules/npm/bin/npm-cli.js'), 'ci',
            '--ignore-scripts', '--offline', '--no-audit', '--no-fund'], cwd=build_root, env=environment)
        if completed.returncode: raise ValueError('Offline restore failed; no network fallback, partial output retained')
        completed = subprocess.run([str(node/'node.exe'), str(repo/'scripts/build-server.mjs'), '--payload', str(output),
            '--root', str(build_root)], cwd=build_root, env=environment)
        if completed.returncode: raise ValueError('Server bundle build failed; partial output retained')
    legal = app/'legal'  # holds the server bundle's record already
    shutil.copyfile(repo/'LICENSE', legal/'Yahad-LICENSE.txt')
    lock = json.loads((repo/'package-lock.json').read_text(encoding='utf-8'))['packages']
    bundled = json.loads((legal/'server-bundle.json').read_text(encoding='utf-8'))['packages']
    (legal/'SBOM.json').write_text(json.dumps(app_sbom(version, source_commit, lock, bundled), indent=2)+'\n', encoding='utf-8', newline='\n')
    schema, migrations = source_schema(repo)
    (legal/'build-inputs.json').write_text(json.dumps({'appSourceCommit':source_commit,
        'packagerSourceCommit':source_commit,
        'databaseSchema':schema, 'databaseMigrationsFrom':migrations,
        'offlineProductionRestore':True,
        'familyInstallationReady':False}, indent=2)+'\n', encoding='utf-8', newline='\n')
    for name in ('no-user-npm-config', 'no-global-npm-config'): (output/name).unlink(missing_ok=True)
    print(json.dumps({'prepared':str(output), 'version':version, 'sourceCommit':source_commit, 'offline':True, 'familyInstallationReady':False}))
    return output


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('repo', 'output', 'node', 'cache'): parser.add_argument('--'+name, required=True, type=Path)
    args = parser.parse_args()
    prepare(args.repo, args.output, args.node, args.cache)
