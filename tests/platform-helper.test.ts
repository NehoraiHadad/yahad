import { test } from 'node:test';
import assert from 'node:assert/strict';
import { platformHelper } from '../src/platform-helper.js';
import { createPlatformFiles, platformFiles } from '../src/platform-files.js';
import { createWindowsUpdateTransport } from '../src/windows-update-transport.js';

// The service host names the Platform image in its own install tree
// (installer/Yahad.ServiceHost/InstalledLaunch.cs). The app derives none.
const refused: [string, NodeJS.ProcessEnv][] = [
  ['missing', {}],
  ['empty', { YAHAD_PLATFORM_HELPER: '' }],
  ['relative', { YAHAD_PLATFORM_HELPER: 'platform\\Yahad.Platform.exe' }],
  ['app-relative', { YAHAD_PLATFORM_HELPER: '..\\native\\Yahad.Platform.exe' }],
  ['drive-relative', { YAHAD_PLATFORM_HELPER: 'C:Yahad.Platform.exe' }],
  ['rooted without a drive', { YAHAD_PLATFORM_HELPER: '\\Program Files\\Yahad.Platform.exe' }],
  ['UNC', { YAHAD_PLATFORM_HELPER: '\\\\server\\share\\Yahad.Platform.exe' }],
  ['POSIX', { YAHAD_PLATFORM_HELPER: '/opt/yahad/Yahad.Platform.exe' }],
  ['forward slashes', { YAHAD_PLATFORM_HELPER: 'C:/Program Files/Yahad/Yahad.Platform.exe' }],
  ['dot segments', { YAHAD_PLATFORM_HELPER: 'C:\\Program Files\\Yahad\\app\\..\\platform\\Yahad.Platform.exe' }],
  ['not an .exe', { YAHAD_PLATFORM_HELPER: 'C:\\Program Files\\Yahad\\platform\\Yahad.Platform.dll' }],
  ['no file name', { YAHAD_PLATFORM_HELPER: 'C:\\Program Files\\Yahad\\platform\\' }],
];

test('the Platform helper is read only from YAHAD_PLATFORM_HELPER and admits an absolute .exe path', () => {
  const image = 'C:\\Program Files\\Yahad.FirstInstall\\infra\\1.0.0\\platform\\Yahad.Platform.exe';
  assert.equal(platformHelper({ YAHAD_PLATFORM_HELPER: image }), image);
  assert.equal(platformHelper({ YAHAD_PLATFORM_HELPER: 'D:\\Y\\YAHAD.PLATFORM.EXE' }), 'D:\\Y\\YAHAD.PLATFORM.EXE');
});

for (const [name, env] of refused) test(`a ${name} YAHAD_PLATFORM_HELPER is refused with no fallback path`, () => {
  assert.throws(() => platformHelper(env), /YAHAD_PLATFORM_HELPER refused/);
  assert.throws(() => createPlatformFiles('win32', env), /YAHAD_PLATFORM_HELPER refused/);
  // Refused on Windows for the value, and off Windows for the platform.
  assert.throws(() => createWindowsUpdateTransport(env), /WINDOWS_UPDATES_UNCONFIGURED/);
});

test('the Linux file boundary never consults the Windows helper variable', () => {
  const linux = createPlatformFiles('linux', {});
  assert.equal(linux.setupCapabilityPath(), '/etc/yachad/setup-capability.json');
});

test('a Windows process without YAHAD_PLATFORM_HELPER refuses its first private-file read', { skip: process.platform !== 'win32' }, () => {
  const previous = process.env.YAHAD_PLATFORM_HELPER;
  delete process.env.YAHAD_PLATFORM_HELPER;
  try {
    assert.throws(() => platformFiles.readMasterKey('C:\\absent\\master-key'), /YAHAD_PLATFORM_HELPER refused/);
    assert.throws(() => createWindowsUpdateTransport(), /WINDOWS_UPDATES_UNCONFIGURED/);
  } finally { if (previous !== undefined) process.env.YAHAD_PLATFORM_HELPER = previous; }
});
