# Yahad landing page

Standalone Hebrew RTL introduction page. The editable HTML, CSS, JavaScript and
local assets live in `landing/dist/`. There is no build step or runtime
dependency installation. The app itself lives in `web/`.

## Preview

From `landing/`, serve the static page:

```sh
python3 -m http.server 4173 --directory dist
```

Open `http://localhost:4173`. The demonstration uses synthetic content. It does
not connect to WhatsApp or send messages. With JavaScript disabled, the seven
steps and a still of the conversation are available. With JavaScript, normal
page scrolling drives the device scenes and message transfers in both directions.
Reduced-motion settings and short viewports show the static version.

## Checks

From `landing/`:

```sh
node --check dist/how.js
node --check dist/reveal.js
node tests/how.test.cjs
node tests/reveal.test.cjs
python3 -X utf8 tests/static.test.py
python3 -X utf8 tests/contrast.test.py
```

The GitHub Pages workflow publishes only `landing/dist/` after changes reach
`main`. Application releases use a separate workflow.

## Download area

Windows points to the fixed `Yahad.Setup.exe` asset in the latest release.
Linux is pending. Each card declares its own state. To activate a tested
installer, change the card to `data-state="ready"` and use a link with
`class="primary download-action"` pointing to the asset in this repository's
GitHub Releases. Update the availability copy and the shipped-state assertions
in `tests/static.test.py` together. Keep the per-platform validator and fixtures.

## Assets

The family illustration is stored locally as WebP. Noto Sans Hebrew fonts and
Lucide icons retain their license notices in `dist/assets/FONT-LICENSE.txt`
and `dist/assets/ICON-LICENSE.txt`.
