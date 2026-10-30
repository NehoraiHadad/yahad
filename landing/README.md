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
