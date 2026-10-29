# Native web installer

Windows x64 native C++/Win32 launcher. It has no .NET, Node, Python, WebView or
Visual C++ redistributable prerequisite. All imported DLLs must be stock Windows
components; the C++ support libraries are linked statically. Build tooling is
not installed on the family's computer.

The launcher downloads the preserved full first-install EXE, not individual
uncoordinated dependencies. This preserves the tested service installer and its
single UAC step. Downloading does not require elevation. The existing installer
shows installation/startup progress and opens the existing web onboarding.
Closing that installer is not reported as observed installation success.

## Distribution gate

`payload-pin.json` identifies the repaired full installer built from
`9fc810d7e8fcb63ed9eb2c1225c5cd95880366a8`: version 0.5.3,
290343109 bytes and SHA256
`c62907fa5a4ae16ded29a398855a177d952fdf902d57aed3ea14f763d77f027c`.
The outer Setup contains the elevation-framing repair. Its unchanged verified
application/service payload remains from `2d1b547cb7cadd0ed08c6cdb3584c78ae9060f51`;
the pin records those two source identities separately.
No public upload or URL has been authorized. The default native artifact clearly
shows that distribution is unconfigured and disables download. It never guesses
a release URL or downloads a different/latest version. An authorized publisher
must supply the immutable HTTPS URL to `build.py --payload-url` and verify the
actual hosted bytes before linking the launcher from the landing page.

The URL is compiled into an internal PE resource; no user-supplied URL, sidecar manifest, environment
override, CLI payload override or unsigned remote version selection is accepted.
Redirects are at most three, HTTPS only, same host; GitHub release downloads may
also redirect to exactly release-assets.githubusercontent.com. Certificate
validation is never disabled. Default Windows proxy configuration is used;
automatic logon is disabled and proxy authentication errors have a useful message.

## Integrity and cancellation

Download and verification stream through bounded buffers. Content length, if
provided, must match; EOF length and SHA256 must always match. Oversize, truncated,
corrupt, unapproved redirect and HTTP-error responses never execute. Cancellation
is checked around blocking calls and chunks; a blocked network call can take its
configured timeout to return (5-second resolve/connect, 10-second send/receive).
There is also a one-hour transfer deadline. Retry starts a fresh private file.

Temporary directories are randomly named under the user's LocalAppData/Temp.
They have a protected current-user/System DACL at creation. Ancestor directory
handles reject reparse points and prevent rename while in use. The sealed file
is opened read-only with no write/delete sharing, checked for links, rehashed,
and held through the existing installer's process lifetime. Only the exact
verified path is passed to CreateProcess; its environment is an explicit small
whitelist without Node/.NET/Python hooks or development PATH entries.

Cancellation applies to download/verification, not a running service install.
Once the existing installer starts, its own UI owns installation and cancellation.
Closing the download window does not kill that installer. Normal completion
removes the tracked temporary EXE and empty directory. A crash or antivirus lock
can leave public installer bytes in this private cache; no recursive cleanup or
deletion of unrelated files is performed. No family data or credentials are
downloaded or created by the bootstrap.

## Scope and evidence

The user payload uses the existing Baileys connector; synthetic mode is selected explicitly only by tests. A new family remains unlinked until the authenticated parent uses the existing web account-link button and QR. Existing linked families retain reconnect behavior. No actual account connection is performed by these tests. The current first-install network profile remains numeric loopback; LAN/TLS exposure is not silently added.
No service registration, persistent machine permissions, firewall, certificates,
network listener or public distribution is performed by the bootstrap tests.

Tests use the same download/hash/file/launch code with synthetic readers and a
harmless native child in a private test directory. The optional HTTP probe uses
the same real WinHTTP intake on an exact official, hash-pinned Zig archive; it
never executes the downloaded ZIP and is not proof of product hosting or service
installation. Test programs are not distribution artifacts.

Run `build.py` with an absolute verified portable Zig compiler and output folder.
The checked source pins Zig 0.15.2 provenance separately; no compiler is embedded
in the launcher. Review static support-library notices and the offline payload's
existing source/license obligations, code signing and clean-host acceptance
before public distribution. No new signing keys or trust enrollment are created.
The native bootstrap includes the LLVM license/exception and MinGW copying text
as an embedded resource, also available with the non-installing `--notices` mode.


Keyboard tab stops and native tab order are checked. Hidden layout fixtures exercise 96/144/192 DPI, bounded scrolling and footer visibility without changing desktop settings. Interactive Computer Use launch approval timed out; these renders are not a claim of interactive keyboard or multiple-monitor acceptance.

The committed payload pin names the framing-repaired full installer from 9fc810d7e8fcb63ed9eb2c1225c5cd95880366a8, with its unchanged application/service payload from 2d1b547cb7cadd0ed08c6cdb3584c78ae9060f51. It uses the real existing connector, with account linking initiated only from existing web onboarding. The distribution URL remains unconfigured and download remains disabled until an authorized publisher supplies the exact retained HTTPS asset. The full offline installer is independently usable for the approved local host test.
