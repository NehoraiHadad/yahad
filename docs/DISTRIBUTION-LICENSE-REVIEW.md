# Distribution license review: open items

This is a technical collection checklist, not a completed legal review or an
agreement accepted for the owner. Public delivery remains gated by
`NATIVE-INSTALL-DISTRIBUTION.md` and the native installer acceptance documents.
Keep `completeLicenseReview: false` until the reviewed distribution arrangement
and its evidence are recorded. Do not describe the complete installer as MIT-only.

The retained development payload from source
`95481660083516bc70c1e1447084d5d1c95545a2` supplies the inventory observations
below. A subsequent build must regenerate its inventories and verify its own
bytes; this document does not attest to a later artifact.

| Component | Retained evidence | Collection still needed |
| --- | --- | --- |
| Yahad application | Root `LICENSE`, MIT | The actual first-install packager now copies this exact file to `legal/Yahad-LICENSE.txt`. The previously retained 9548166 payload predates that correction. Verify the copy in the next payload manifest; do not relabel the old payload as corrected. |
| Installed npm dependencies | `legal/installed-npm-inventory.json`, 172 installed package roots, lock integrity and notice hashes | Preserve that inventory and all package notices. Its package-root scan is explicitly incomplete for embedded/native components. Four roots have no recorded root notice: `@esbuild/win32-x64` 0.25.12, `@tokenizer/token` 0.3.0, `abstract-logging` 2.0.1 and `whatsapp-rust-bridge` 0.5.4. |
| Existing npm notice mappings | Matching `esbuild` 0.25.12 `LICENSE.md`; `@tokenizer/token/README.md` contains its MIT text | Record exact source path/hash/version mapping for these two. Empty root-notice records alone do not prove license text is absent. Inspect exact-version upstream material for `abstract-logging` and the Rust/WASM bridge before collecting missing attribution or sources. Avoid inventing copyright holders or substituting current upstream files for pinned-version evidence. |
| libsignal 6.0.0 | Installed metadata declares GPL-3.0; its license and JS sources are retained | Record the owner's reviewed treatment of this component within the intended combined distribution and a source-delivery arrangement. The metadata finding alone is not a compatibility or violation verdict. |
| Sharp / native libvips | `@img/sharp-win32-x64` declares Apache-2.0 AND LGPL-3.0-or-later; package README has 29 native library rows; `versions.json` pins 28 component versions including libvips 8.18.7 | Map the actual native library versions to retained license/notice text and applicable source material. Sharp's top-level Apache text does not complete the native transitive inventory. Keep exact provenance and hashes. |
| FFmpeg BtbN build | `installer/windows/dependency-pins.json`, retained `legal/ffmpeg-build.json`, archive/checksum pins and build configuration | Collect and verify corresponding sources and build inputs for `n9.0.2-37-gae19868753`, release `autobuild-2026-10-28-19-47`, plus its enabled transitive components. The inspected payload has no complete corresponding-source collection. Its pending review flag remains accurate. |
| Caddy 2.11.7 | Pinned binary; retained build-info has 148 dependency entries; `legal/Caddy-NOTICES.txt` explicitly leaves transitive review pending | Produce an exact module-version-to-license/notice inventory and retain required attribution/source material. Packaging attribution alone does not complete this review. |
| Node / .NET / Bootstrap toolchain | Existing Node license bundle, .NET license and third-party notices; Bootstrap LLVM/MinGW notices | Preserve these files and link them into the final combined inventory. Review the actual published artifact's runtime closure, including Windows desktop/runtime inputs. Do not infer full coverage from the packager's main SDK notice copy. |

Local work can collect already available texts, make exact-version mappings,
hash the files, add omitted project notices, and regenerate inventories without
credentials or accepting agreements. Retrieval of missing source material should
use identified upstream releases and retain provenance. Any source-hosting or
distribution commitment belongs in the owner's approved release arrangement.

Owner review is still needed for the combined distribution treatment, the chosen
source-delivery arrangement and its terms, and the signing/distribution policy.
No signing identity has been created or enrolled. This review does not satisfy
actual-machine acceptance, signature trust, public publication approval, or
tests that have not run.
