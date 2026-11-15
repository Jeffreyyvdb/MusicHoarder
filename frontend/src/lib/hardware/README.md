# Hardware skin (easter egg)

MusicHoarder redesigned as a [SPEC-01](https://github.com/shuding/spec-01) / Teenage Engineering
style instrument, not a recoloured app: the sidebar and the page are two molded devices on a paper
desk, every list is data on a grey LCD (a black screen in dark mode), buttons are keycaps on a lip
(white, one red primary, black for the transport), names are printed in small spaced capitals with
a red LED, and the selection is red between two dashes. Some detail goes on purpose: the nav and
chip icons, the tracks table's covers and its size, match and source columns, the cover wash on the
album page and behind Now Playing. Now Playing becomes the MH-1, a device built from SPEC-01's
components on Vercel's [UI SDK](https://ui-sdk.dev), sitting on the same desk, and its Video mode
plays the clip on the MH-2, a monitor beside it.

It is hidden. It unlocks with the **Konami code** (↑ ↑ ↓ ↓ ← → ← → B A, anywhere outside a text
field) or by searching for **`spec-01`** (phones have no arrow keys). Finding it switches it on and
adds a **Hardware** switch (and **Key sounds**) under Appearance in the account menu. Per browser,
like Appearance: `localStorage['mh:hardware']` is absent (locked), `on` or `off`.

## How it hangs together

- `hardware-mode.svelte.ts` — the state, the Konami matcher and the secret phrases. Sets
  `data-mh-skin="hardware"` on `<html>`; `app.html` does the same before the first frame, and the
  root layout follows it for `theme-color` (`HARDWARE_THEME_COLOR`, mirrored in `app.html`).
- `app-skin.css` — the whole app under the skin. Not imported from `app.css`: it is its own
  stylesheet, requested only while the skin is on (`skin-stylesheet.ts`) — by `app.html`'s
  first-paint script as a render-blocking `<link>` on a reload (the server fills in its hashed URL,
  `hooks.server.ts`), and by `hardware-mode.svelte.ts` when the skin is switched on. Its header lists the
  vocabulary (desk, body, LCD, keys, labels, LED) and every hook it styles: the primitives by
  `data-slot` (plus `filter-chip`), Button's `data-variant`, `.mh-chrome`, `.mh-cover` / `.mh-cover-img`, and a few
  `data-mh-*` attributes in the shell, TrackList, AlbumPage, By folder, the mini player and the tab
  bar. Those attributes style nothing without the skin; renaming one means following it there. The
  tracks table re-declares TrackList's column grid with fewer columns, so a new column there must be
  added to it too. Unlayered on purpose, so every rule must stay under
  `:root[data-mh-skin='hardware']` — with the skin off nothing here applies.
- Two behaviour changes ride on the flag, both read from `hardwareMode.enabled`: TrackList's desktop
  rows are 40px instead of 56px (no art), and Now Playing (`SongDetailHost`) drops its forced-dark
  media appearance and cover wash for the desk, so `theme-color` stays the skin's there too.
- `HardwareNowPlaying.svelte` → `HardwarePlayer.svelte` — the device, which `TrackPanel` loads on
  demand in place of the art, title and transport for the loaded song (not while the music plays on
  another device). Lazy because its stylesheet, the vendored skin, is ~150 KB gzipped.
- `HardwareVideo.svelte` — the MH-2: the bezel, plate, sync LCD and fullscreen key around Video
  mode's picture. It is a *housing* for `VideoWatchTab` (its `housing` prop), which keeps the
  picture, its sync and fullscreen, renders the frame as the bezel's `display-screen`, and fits the
  picture into the stage less whatever the housing measures around it. Hand-built rather than a
  `Device`, which zooms itself to fit and would scale the fullscreen overlay inside it. Loaded on
  demand from `TrackPanel`, like the MH-1; unlike the MH-1 it also houses a browsed song's clip,
  since its one key works for any song.
- `behaviors/` — haptics, key sounds, keyboard-only focus rings and the block caret, loaded only once
  the skin is on.

## What is vendored, and from where

- `skin/` — SPEC-01's stylesheets, verbatim, from `registry/hardware/ui/hardware/` at commit
  `b8f91b6646d9280d9de8e0f5b2c6453cb4f237cb`. MIT, © Shu Ding (`./LICENSE`). Keep them verbatim so
  they can be refreshed; MusicHoarder's changes go in `musichoarder.css` and `app-skin.css`.
  They load with the device, and stay in the document once loaded. Most of their rules are under
  `[data-skin="hardware"]`, but `devices.css` and `layout.css` also carry bare attribute selectors
  (`[data-row]`, `[data-section]`, `[data-art]`, `[data-kit="field"]`, …). Nothing outside the
  device uses those attributes; keep it that way, or the skin's layout reaches the app after the
  switch is turned off.
- `behaviors/` — SPEC-01's page-wide behaviours, same commit and licence, with local changes:
  - everything checks `skinActive()` (`active.ts`) first, because these listen at the document and
    cannot be uninstalled — switching the skin off must make the app feel exactly as before (the
    caret only follows a skin field, and the popup watcher only works while sounds are on);
  - the app's own buttons, switches, rows and tabs are added to the haptic and sound selectors;
  - sounds are **off unless turned on** (a second Web Audio context clicking over the music could
    disturb the iOS audio session that background playback relies on);
  - Safari's Tab-order patch is left out (it retabs every button on the page).
- `/static/fonts/spec-mono/` — SPEC-01's subset of IBM Plex Mono, OFL 1.1 (`OFL.txt` beside it).
  Self-hosted because the CSP's `font-src` is `'self'`.
- `Icon.svelte` — path data from IBM's Carbon icons, Apache-2.0 (`./CARBON-LICENSE`).

## What is ported

SPEC-01 ships React components only. The ones the device needs are ported to Svelte 5 on
`@ui-sdk/svelte`, keeping the `data-kit` attributes the skin styles: `Hardware`, `Device`,
`Display`, `Lcd`/`LcdDigits`, `ButtonGroup` (SDK `Button`), `Latch` (SDK `Checkbox`), `FlatKnob`
(SDK `NumberInput` scrub area), `Slider` (SDK `Field` + `Slider`) and `ItemList` (SDK `Listbox`).
When refreshing `skin/`, diff SPEC-01's React files for these components too: a changed wrapper or
`data-kit` name there must be followed here, or the skin quietly misses the element.
