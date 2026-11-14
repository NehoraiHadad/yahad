# Yahad landing page

Standalone Hebrew RTL introduction page, published to GitHub Pages. Everything
that is served lives in `landing/dist/`, exactly as stored. There is no build step
and nothing to install. The app itself lives in `web/`.

## Preview

From `landing/`:

```sh
python3 -m http.server 4173 --directory dist
```

Open `http://localhost:4173`. The page uses synthetic content only. It never
connects to WhatsApp and never sends messages.

## Structure

| File | Role |
| --- | --- |
| `dist/index.html` | All content: opening, how it works, control, screen time, home setup, install steps, risk notice, FAQ, download |
| `dist/style.css` | Tokens (`:root`), fonts and every section except the explainer |
| `dist/how.css`, `dist/how.js` | The "how it works" explainer |
| `dist/404.html` | Not-found page (absolute `/yahad/` paths, because Pages serves it from any depth) |
| `dist/assets/` | Fonts, illustration, share image, icons and their licenses |
| `tools/build-assets.py` | Regenerates the derived assets (see below) |

Color carries meaning across the page: `--pass` (green) is what reaches the
child, and `--held` (amber) is what stays with the parent.

## How it works (explainer)

- **The content is the seven steps.** `<ol class="how-steps">` holds real `h3` and `p`
  text in normal reading flow. Without JavaScript, or without
  `IntersectionObserver`, it renders as a plain numbered list and the stage is
  hidden.
- **The stage is decoration.** `.how-stage` (`aria-hidden`) holds the parent's
  phone, the home computer running Yahad (`.hub`) and the child's tablet, all
  drawn in CSS. Every message passes through the computer: the QR shows there in
  step 1, approved messages go through it, and an unapproved one stops there
  (step 6). The stage sits before the steps in the DOM, because on narrow screens
  it is `position: sticky` in block flow.
- **One number drives the stage.** `how.js` adds `.how-live` and watches a thin
  reading line: the middle of the screen when wide, and below the sticky stage
  when narrow. The margins are set in px from `innerHeight` and rebuilt when it
  changes, because `rootMargin` percentages resolve against the width. The step
  crossing that line becomes `data-step` on the section. Every screen, packet,
  line and lock in `how.css` is a rule keyed on `[data-step]`.
- **The camera.** The devices sit at fixed coordinates in one of two "worlds":
  side by side (1180×680) or stacked (600×1300). `how.js` sets `data-world` to
  whichever overview shows larger in the current stage, so a portrait stage on a
  desktop gets the stacked world too. It then only scales and translates
  `.how-cam` to fit the rectangle a step is about (`WORLD`, `TARGET`). The world
  rectangles must match the device boxes in `how.css`, and
  `tests/static.test.py` enforces this.
- **Motion.** Entrances are CSS animations whose end state is the static rule.
  With reduced motion the animations are turned off and the same states still
  appear, without movement.

To add or reorder a step: edit the `<li>` list, its `[data-step]` rules in
`how.css` and `TARGET` in `how.js`, then the expected order in
`tests/static.test.py`.

## Checks

From `landing/` (the Pages workflow runs the same commands):

```sh
node --check dist/how.js
node tests/how.test.cjs
python3 -X utf8 tests/static.test.py
python3 -X utf8 tests/contrast.test.py
```

- `how.test.cjs`: runs `how.js` against a stubbed DOM. It checks inert
  fallbacks, the px reading line and its rebuild on resize, `data-step` and
  `.is-active`, the world choice, camera aim and the zoom cap.
- `static.test.py`: markup validity, anchors and assets, step order, stage and
  world sync, share tags, font coverage of every visible character, and the
  download-card states.
- `contrast.test.py`: WCAG 4.5:1 for every token pair in use, including the
  explainer text on each of its background stops.

None of these renders the page. Before merging, scroll through by hand at a
phone width and a desktop width, and once with reduced motion on.

The Pages workflow publishes only `landing/dist/`, only after changes reach
`main`, and only the file types it allows (`.html .css .js .svg .webp .woff2 .jpg .png .txt`).
Application releases use a separate workflow.

## Download area

Windows points to the fixed `Yahad.Setup.exe` asset in the latest release.
Linux is pending. Each card declares its own state. To activate a tested
installer, change the card to `data-state="ready"` and use a link with
`class="primary download-action"` pointing to the asset in this repository's
GitHub Releases. Update the availability copy and the shipped-state assertions
in `tests/static.test.py` together. Keep the per-platform validator and fixtures.

## Assets

`tools/build-assets.py` regenerates `hebrew.woff2`, `latin.woff2`,
`family-moment-768.webp`, `og-image.jpg` and `apple-touch-icon.png`. It needs
`fonttools`, `brotli` and `pillow`, plus the variable Noto Sans Hebrew and
Noto Sans fonts from google/fonts:

```sh
python3 -X utf8 tools/build-assets.py --hebrew "NotoSansHebrew[wdth,wght].ttf" --latin "NotoSans[wdth,wght].ttf"
```

The two font files form one family, `Yahad`, split by `unicode-range`. The
ranges in the tool and in `style.css` must match. Licenses are in
`dist/assets/FONT-LICENSE.txt` (SIL OFL 1.1) and `dist/assets/ICON-LICENSE.txt`
(Lucide).
`dist/assets/nehorai.svg` is the author's own logo, used for the footer credit
(a link to `https://maklikim.co.il/l/me`, which redirects to nehoraihadad.com).
