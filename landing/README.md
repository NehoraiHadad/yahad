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
| `dist/film-scene.js` | The "how it works" film: `YahadFilm.render(t)` returns frame `t` as SVG |
| `dist/film.js`, `dist/film.css` | Plays the film beside the four beats |
| `dist/404.html` | Not-found page (absolute `/yahad/` paths, because Pages serves it from any depth) |
| `dist/assets/` | Fonts, illustration, share image, icons and their licenses |
| `tools/build-assets.py` | Regenerates the derived assets (see below) |

Color carries meaning across the page: `--pass` (green) is what reaches the
child, and `--held` (amber) is what stays with the parent.

## How it works (film)

- **The content is the four beats.** `<ol class="beats">` holds real `h3` and `p`
  text: Noa wants to write to her friends, you choose what she sees, she signs in on
  her own, her friends stay on WhatsApp. Without JavaScript or
  `IntersectionObserver` it renders as a plain numbered list and the film is
  hidden. Setup details (the home computer, the QR) belong in the home setup
  section, not here; `tests/static.test.py` enforces this.
- **The film is decoration.** `film-scene.js` draws a 23.5 s film, deterministic
  per frame: `YahadFilm.render(t)` returns a 1080×1920 SVG string. It was made
  for the promo video (same scenes and captions) and uses a small subset of the
  Anatomy kit's isometric helpers. SVG ids are prefixed `film-` because the frame
  is inlined into the page.
- **The player.** `film.js` adds `.film-live` and shows `.film`. It plays once
  when half the film is on screen (about 30 fps), pauses when it leaves, and the
  button pauses, resumes or replays. Each beat's `data-at` is where it starts in
  the film. Its heading becomes a button that jumps there (scrolling the film
  back into view first if it is off screen), and the beat being shown gets
  `.is-active`. Caption pills are sized from the real text width in the page
  font (`YahadFilm.setMeasure`). With reduced motion nothing plays by itself: each beat
  shows its `data-still` frame, and the button plays the film on request.

To change a beat: edit its `<li>`, keeping `data-at` ascending and `data-still`
inside the beat. `tests/film.test.cjs` checks this against the film's length.

## Checks

From `landing/` (the Pages workflow runs the same commands):

```sh
node --check dist/film-scene.js
node --check dist/film.js
node tests/film.test.cjs
python3 -X utf8 tests/static.test.py
python3 -X utf8 tests/contrast.test.py
```

- `film.test.cjs`: renders every film frame (valid SVG, namespaced ids,
  deterministic), checks the beats against the film, and runs `film.js` against
  a stubbed DOM: autoplay at half visibility, pause and hold, replay, beat
  jumps, 30 fps, no fast-forward after a hidden tab, reduced motion.
- `static.test.py`: markup validity, anchors and assets, the beats, share tags,
  font coverage of every visible character (including the film's text), and the
  download-card states.
- `contrast.test.py`: WCAG 4.5:1 for every token pair in use, including the
  beats and the film button.

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
