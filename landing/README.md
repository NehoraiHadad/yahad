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
| `dist/index.html` | Opening illustration, short introduction, installer link, film, and a closed technical disclosure |
| `dist/style.css` | Tokens (`:root`), fonts and every section except the explainer |
| `dist/film-scene.js` | The "how it works" film: `YahadFilm.render(t)` returns frame `t` as SVG |
| `dist/film.js`, `dist/film.css` | Inline film player and its styles |
| `dist/404.html` | Not-found page (absolute `/yahad/` paths, because Pages serves it from any depth) |
| `dist/assets/` | Fonts, illustration, share image, icons and their licenses |
| `tools/build-assets.py` | Regenerates the derived assets (see below) |

## Homepage

The opening is one column with the illustration, a short description, and the
Windows installer link. The link points to the fixed `Yahad.Setup.exe` asset
on the latest GitHub release. The native disclosure below the film contains
system requirements, installation, the WhatsApp connection notice, and the FAQ.

The film is a deterministic 1080×1920 SVG animation. It plays once when half
of the frame is visible, pauses off screen, and can be paused, resumed or
replayed. Reduced motion starts on a still frame and plays on request. A
text description is available to assistive technology and when the player
cannot initialize.

## Checks

From `landing/` (the Pages workflow runs the same commands):

```sh
node --check dist/film-scene.js
node --check dist/film.js
node tests/film.test.cjs
python3 -X utf8 tests/static.test.py
python3 -X utf8 tests/contrast.test.py
```

- `film.test.cjs` renders each frame and checks autoplay, pause, replay,
  timing, and reduced motion.
- `static.test.py` checks markup, local assets, font coverage, the visible
  installer link, the film description, and the closed technical disclosure.
- `contrast.test.py` checks text colors against their backgrounds.

The Pages workflow publishes only `landing/dist/` after changes reach
`main`. Application releases use a separate workflow.

## Assets

`tools/build-assets.py` regenerates `hebrew.woff2`, `latin.woff2`,
`friends-at-the-park-768.webp`, `og-friends.jpg` and `apple-touch-icon.png`. It needs
`fonttools`, `brotli` and `pillow`, plus the variable Noto Sans Hebrew and
Noto Sans fonts from google/fonts:

```sh
python3 -X utf8 tools/build-assets.py --hebrew "NotoSansHebrew[wdth,wght].ttf" --latin "NotoSans[wdth,wght].ttf"
```

The two font files form one family, `Yahad`, split by `unicode-range`. The
ranges in the tool and in `style.css` must match. Licenses are in
`dist/assets/FONT-LICENSE.txt` (SIL OFL 1.1) and `dist/assets/ICON-LICENSE.txt`
(Lucide).
`friends-at-the-park-768.webp` is also drawn inside the film (Noa’s portrait and the end card). The film uses a separate crop, `mia.webp`, for Mia’s portrait.
`dist/assets/nehorai.svg` is the author's own logo, used for the footer credit
(a link to `https://maklikim.co.il/l/me`, which redirects to nehoraihadad.com).
