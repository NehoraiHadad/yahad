"""Developer-only offline core fixture, never the family installer.

Supply private tooling extracted from checksum-verified official Node/.NET SDK
archives. npm and the bundler run here on the build machine only; output holds
one self-contained server bundle per entry, so the family's machine has no
node_modules, TypeScript loader or npm install.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from .application_version import source_release_version, require_native_build_identity, describe_build_artifact
from .setup_package import source_schema


def stage_public_lan_help(repo, app):
    """Only these two public source assets belong in the native app payload."""
    repo, app = Path(repo), Path(app)
    names = ('lan-help.html', 'lan-capabilities.json')
    sources = [repo/'deploy'/name for name in names]
    if not any(path.exists() for path in sources): return []  # pre-LAN checkpoints
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


def prepare(repo, output, node, dotnet, cache):
    repo, output, node, dotnet, cache = (Path(p).resolve() for p in (repo, output, node, dotnet, cache))
    # A clean tracked source checkpoint makes the recorded source commit useful.
    if subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=no'], cwd=repo, text=True).strip():
        raise ValueError('Commit tracked source before preparing a reproducible fixture')
    source_commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip()
    version=source_release_version(repo)
    require_native_build_identity(describe_build_artifact(repo/'installer/artifacts/Yahad.Platform.Bundle/Yahad.Platform.exe','--describe',repo),
        'Yahad.Platform',version,source_commit)
    if output.exists() or output == repo or not cache.is_dir(): raise ValueError('New output and an existing offline cache required')
    output.mkdir(parents=True, exist_ok=False)
    app = output/'app'; app.mkdir()
    shutil.copyfile(repo/'package.json', app/'package.json')
    for name in ('dist', 'fixtures'): shutil.copytree(repo/name, app/name)
    stage_public_lan_help(repo, app)
    shutil.copytree(repo/'installer/artifacts/Yahad.Platform.Bundle', app/'native')
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
        if completed.returncode: raise ValueError('Offline restore failed; no network fallback, partial fixture retained')
        completed = subprocess.run([str(node/'node.exe'), str(repo/'scripts/build-server.mjs'), '--payload', str(output),
            '--root', str(build_root)], cwd=build_root, env=environment)
        if completed.returncode: raise ValueError('Server bundle build failed; partial fixture retained')
    runtime = output/'runtime/node'; runtime.mkdir(parents=True); shutil.copyfile(node/'node.exe', runtime/'node.exe')
    legal = output/'legal'  # holds the server bundle's record already
    shutil.copyfile(repo/'LICENSE', legal/'Yahad-LICENSE.txt')
    shutil.copyfile(node/'LICENSE', legal/'Node-LICENSE.txt')
    shutil.copyfile(dotnet/'LICENSE.txt', legal/'DotNet-LICENSE.txt')
    shutil.copyfile(dotnet/'ThirdPartyNotices.txt', legal/'DotNet-ThirdPartyNotices.txt')
    # Every bundled package with its registry identity. Its package-root legal texts
    # are in ThirdPartyNotices-server.txt, written by the bundler from the same tree.
    lock = json.loads((repo/'package-lock.json').read_text(encoding='utf-8'))['packages']
    bundled = json.loads((legal/'server-bundle.json').read_text(encoding='utf-8'))['packages']
    (legal/'SBOM.json').write_text(json.dumps({'format':'yahad-development-inventory', 'serverBundleDependencies':[
        {'path':item['path'], 'version':lock[item['path']]['version'], 'license':lock[item['path']].get('license'),
         'integrity':lock[item['path']].get('integrity')} for item in bundled]}), encoding='utf-8')
    def digest(path):
        with path.open('rb') as stream: return hashlib.file_digest(stream, 'sha256').hexdigest()
    schema, migrations = source_schema(repo)
    (legal/'build-inputs.json').write_text(json.dumps({'appSourceCommit':source_commit,
        'helperSourceCommit':source_commit, 'packagerSourceCommit':source_commit,
        'databaseSchema':schema, 'databaseMigrationsFrom':migrations,
        'offlineProductionRestore':True,
        'familyInstallationReady':False}, indent=2), encoding='utf-8')
    version = subprocess.check_output([str(node/'node.exe'), '--version'], env=environment, text=True).strip().removeprefix('v')
    components = [dict(id='node', path='runtime/node/node.exe', version=version,
        sourceUrl=f'https://nodejs.org/dist/v{version}/node-v{version}-win-x64.zip', sha256=digest(runtime/'node.exe')),
        dict(id='platform', path='app/native/Yahad.Platform.exe', origin='project-built', sourceCommit=source_commit,
        sourceUrl='https://github.com/NehoraiHadad/yahad', sha256=digest(app/'native/Yahad.Platform.exe'))]
    (legal/'runtime-lock.json').write_text(json.dumps({'schema':1, 'target':'win-x64', 'components':components}), encoding='utf-8')
    print(json.dumps({'prepared':str(output), 'profile':'core-fixture', 'sourceCommit':source_commit, 'offline':True, 'familyInstallationReady':False}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('repo', 'output', 'node', 'dotnet', 'cache'): parser.add_argument('--'+name, required=True, type=Path)
    args = parser.parse_args()
    prepare(args.repo, args.output, args.node, args.dotnet, args.cache)
