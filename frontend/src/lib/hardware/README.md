# Hardware skin (easter egg)

MusicHoarder dressed as a [SPEC-01](https://github.com/shuding/spec-01) pocket instrument: molded
off-white panels on a dotted desk, keys that stand on their bases, LCDs, one orange key colour and
IBM Plex Mono — and Now Playing becomes the MH-1, a device built from SPEC-01's components on
Vercel's [UI SDK](https://ui-sdk.dev).

It is hidden. It unlocks with the **Konami code** (↑ ↑ ↓ ↓ ← → ← → B A, anywhere outside a text
field) or by searching for **`spec-01`** (phones have no arrow keys). Finding it switches it on and
adds a **Hardware** switch (and **Key sounds**) under Appearance in the account menu. Per browser,
like Appearance: `localStorage['mh:hardware']` is absent (locked), `on` or `off`.

## How it hangs together

- `hardware-mode.svelte.ts` — the state, the Konami matcher and the secret phrases. Sets
  `data-mh-skin="hardware"` on `<html>`; `app.html` does the same before the first frame, and the
  root layout follows it for `theme-color` (`HARDWARE_THEME_COLOR`, mirrored in `app.html`).
- `app-skin.css` — the whole app under the skin, imported from `app.css`. Tokens for light and a
  graphite dark, plus the primitives by their `data-slot` (buttons as keys, switches, sliders, fields,
  panels), `.mh-chrome` (molded tab bar and mini player), `.mh-cover` (covers in a bezel) and
  `[data-mh-lcd]` (the mini player's title). Unlayered on purpose, so every rule must stay under
  `:root[data-mh-skin='hardware']` — with the skin off nothing here applies.
- `HardwareNowPlaying.svelte` → `HardwarePlayer.svelte` — the device, which `TrackPanel` loads on
  demand in place of the art, title and transport for the loaded song (not while the music plays on
  another device). Lazy because its stylesheet, the vendored skin, is ~150 KB gzipped.
- `behaviors/` — haptics, key sounds, keyboard-only focus rings and the block caret, loaded only once
  the skin is on.

## What is vendored, and from where

- `skin/` — SPEC-01's stylesheets, verbatim, from `registry/hardware/ui/hardware/` at commit
  `b8f91b6646d9280d9de8e0f5b2c6453cb4f237cb`. MIT, © Shu Ding (`./LICENSE`). Keep them verbatim so
  they can be refreshed; MusicHoarder's changes go in `musichoarder.css` and `app-skin.css`.
- `behaviors/` — SPEC-01's page-wide behaviours, same commit and licence, with local changes:
  - everything checks `skinActive()` (`active.ts`) first, because these listen at the document and
    cannot be uninstalled — switching the skin off must make the app feel exactly as before;
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
