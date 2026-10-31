# Yahad landing page — V6c

Standalone Hebrew RTL introduction page. This directory is separate from the
family-chat application in `web/` and its generated root `dist/` output.
`landing/dist/` contains the editable HTML/CSS/JavaScript and local assets;
there is no build step or runtime dependency installation.

## Preview

From `landing/`, serve `dist/` with any static HTTP server, for example:

```sh
python3 -m http.server 4173 --directory dist
```

This is a local preview, not a deployment. Do not run the application release
packager to preview or publish this page.

## Checks

From `landing/`:

```sh
node --check dist/demo.js
node --check dist/reveal.js
node tests/demo.test.cjs
node tests/reveal.test.cjs
python3 tests/static.test.py
python3 tests/contrast.test.py
```

The tests cover finite demo motion, pause/replay/manual steps, reduced-motion
and visibility behavior, reveal error/focus/fragment fallbacks, internal assets
and links, RTL source order, installer wording and text contrast.

The page uses synthetic demonstration content only. It does not connect to
WhatsApp, send messages, collect data or offer a currently unavailable download.
The approved visual direction, contrast corrections and optional motion are
integrated. Browser validation of the exact integrated revision is still a
separate review gate; unit tests do not replace keyboard, zoom and visual QA.

## Publication boundary

This branch is for source collaboration only. No deployment, release tag,
installer publication, GitHub Actions workflow or root application build changes
are part of it. Keep the unavailable-installer notice until tested installers
are explicitly approved for release. Existing app packaging excludes `landing/`.

## Assets

The family illustration is project-specific generated artwork, stored locally
as WebP. Noto Sans Hebrew fonts and Lucide icons retain their license notices
in `dist/assets/FONT-LICENSE.txt` and `dist/assets/ICON-LICENSE.txt`.
## Download area

`dist/index.html` contains a `download` section with one card per platform
(Windows, Linux), each in `data-state="pending"` with a disabled button. The
platforms are independent: either one can go live without the other.

Activating a ready, tested installer for one platform:

1. In that card, replace the disabled button with
   `<a class="primary download-action" href="RELEASE-ASSET-URL">`, pointing at
   the real file under this repo's GitHub Releases
   (`https://github.com/NehoraiHadad/yahad/releases/download/...`).
2. Flip the card's `data-state` to `ready` and update its status text.
3. Only after separate release approval, update all availability copy (hero,
   download introduction, FAQ and connection disclosure) to accurately describe
   which platform is ready. Update the explicitly pending-only shipped-page
   assertions in `tests/static.test.py` in the same commit; retain the per-platform
   validator and its positive/negative fixtures. The current assertions deliberately
   reject any ready state or release URL until that reviewed activation change.

`tests/static.test.py` enforces the contract: a pending card carries no link,
a ready card must carry exactly one link to a release asset of this repo, and
no release URL ships while pending. Fixture runs cover windows-only,
linux-only and both-ready, plus rejected non-repo URLs.
