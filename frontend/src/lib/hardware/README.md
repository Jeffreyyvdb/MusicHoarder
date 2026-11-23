# Hardware skin (easter egg)

**A skin changes look and feel, never what is reachable.** Every feature, piece of information and
control the app offers without the skin is there with it. The skin may restyle anything; it may not
take anything away.

The look is a [SPEC-01](https://github.com/shuding/spec-01) / Teenage Engineering style instrument:
the sidebar and the page are two molded devices on a paper desk, every list is data on a grey LCD (a
black screen in dark mode), buttons are keycaps on a lip (white, one red primary, black for the
transport), names are printed in small spaced capitals with a red LED, and the selection is red
between two dashes. Now Playing is the MH-1, the app's own player dressed as a device, and its
Video mode plays the clip on the MH-2, a monitor built from SPEC-01's components on Vercel's
[UI SDK](https://ui-sdk.dev). A music video still plays behind Now Playing, as an old CRT
television: scanlines, a vignette, a rolling bar and a power-on line.

It is hidden. It unlocks with the **Konami code** (↑ ↑ ↓ ↓ ← → ← → B A, anywhere outside a text
field) or by searching for **`spec-01`** (phones have no arrow keys). Finding it switches it on and
adds a **Hardware** switch (and **Key sounds**) under Appearance in the account menu. Per browser,
like Appearance: `localStorage['mh:hardware']` is absent (locked), `on` or `off`.

## The contract

A skin is a stylesheet over the app's own elements, plus look values. It never renders its own copy
of a surface: the first MH-1 was a component that re-implemented Now Playing, and whatever its prop
list did not carry (Shuffle, playback speed, the artist and album links, the badges, the meta line)
was gone under the skin. Three checks keep it that way, one per way a gap can be made.

The MH-1 case places the shared art, title and transport in a compact grid, with a queue preview
for a loaded track. The case depends on appearance and available space. A track playing or paused
on another device keeps the case and the remote-device status.

- **The stylesheet hides only decoration** (`skin-contract.test.ts`, in `bun run test`). It reads
  `app-skin.css`, `musichoarder.css` and any `<style>` block in this folder, and fails on a
  declaration that takes an element off the screen or out of reach (`display: none`, `visibility`,
  `opacity: 0`, zero sizes or scale, a full clip, `pointer-events: none`) unless the selector's
  subject is a pseudo-element or an element marked as decoration. It also fails when the skin sets
  the tracks table's columns (`TrackList`'s `GRID` is the only definition of the table, and a skin
  that needs room changes type or spacing), and when a `skinLook` value would hide something
  (`hidden`, `sr-only`, `invisible`, `0`).
- **Components do not read the flag** (Oxlint, `no-restricted-imports`). `hardwareMode` and
  `skinActive` may be imported in this folder, and `hardwareMode` by the three places that switch
  the skin (the root layout, the account menu, the command palette). Everything else asks for a look value:
  `skinLook(plain, skinned)` returns a class, an attribute value or a size (Now Playing's
  surface), `mediaAppearance()` is the class for Now Playing's media appearance (its overlay and the menus
  opened from it), and `skinHousing(frame)` hands a component a snippet that draws _around_
  content it still renders. These checks stop the usual ways; a determined bypass (a dynamic
  import, `data-mh-skin` read off `<html>`) is what the comparison below is for.
- **The deployed page is compared with itself** (`perf/src/web/preview-checks.ts`, on every PR
  preview). The tracks list, and Now Playing with a song playing and with one only browsed, are
  loaded with the skin off and on, and any control
  or column head present without the skin and missing with it fails the check. Add a scene there
  when a page gets a skin section of its own.

**To drop decoration under the skin**, mark the element where it is rendered and hide it by the
marker. The mark is the element's own claim that nobody reads or uses it, so it sits where the
next person adding content to that element will see it:

```svelte
<div aria-hidden="true" data-mh-decor="wash" class="…" style:background={heroWash}></div>
```

```css
:root[data-mh-skin='hardware'] [data-mh-decor='wash'] {
  display: none;
}
```

The test refuses the marker on anything interactive or without `aria-hidden="true"`. Marked today:
the album hero's wash, the sidebar's logo (its name is beside it) and Now Playing's blurred cover
with its dim. Everything else is restyled, never hidden.

## How it hangs together

- `hardware-mode.svelte.ts` — the state, the Konami matcher and the secret phrases. Sets
  `data-mh-skin="hardware"` on `<html>`; `app.html` does the same before the first frame, and the
  root layout follows it for `theme-color` (`HARDWARE_THEME_COLOR`, mirrored in `app.html`). Also
  the three things a component may ask the skin: `skinLook`, `mediaAppearance`, `skinHousing`.
- `app-skin.css` — the whole app under the skin. Not imported from `app.css`: it is its own
  stylesheet, requested only while the skin is on (`skin-stylesheet.ts`) — by `app.html`'s
  first-paint script as a render-blocking `<link>` on a reload (the server fills in its hashed URL,
  `hooks.server.ts`), and by `hardware-mode.svelte.ts` when the skin is switched on. Its header lists the
  vocabulary (desk, body, LCD, keys, labels, LED) and every hook it styles: the primitives by
  `data-slot` (plus `filter-chip`), Button's `data-variant`, `.mh-chrome`, `.mh-cover` / `.mh-cover-img`, and a few
  `data-mh-*` attributes in the shell, TrackList, AlbumPage, By folder, the mini player, the tab
  bar and Now Playing. Those attributes style nothing without the skin; renaming one means
  following it there. Unlayered on purpose, so every rule must stay under
  `:root[data-mh-skin='hardware']` — with the skin off nothing here applies.
- **The MH-1 is a section of `app-skin.css`**, not a component. `TrackPanel` renders one tree
  whatever the skin, and names its parts with `data-mh-np`: `body` (the player, which carries
  `data-playing`), `screen` (the cover), `readout` (the title block and meta line), `keys` (the
  scrubber, times and transport), `modes` (Lyrics, Devices, Video, Info, Queue) and `volume`, in
  the wide, phone and sideways layouts alike. The stylesheet dresses them: a molded case at lg, a
  printed plate with an LED lit while the song plays, the cover in its bezel, the readout on an
  LCD, the transport as one black rocker (a cap on a side wall that tilts toward the pressed end)
  with the scrubber as a fader, Like and Mute as SPEC-01's latching keys (a matte cap standing out
  of a black base, down while on), the volume as its FlatKnob (a white cap with a red dot over a
  tick ring, the number and the name under it), and the mode row as a second rocker in the kit's
  white, the zone of the mode that is on latched with its LED lit. Both rockers are one rule set:
  `data-mh-rocker` marks the row, and a tone is a handful of `--rk-*` values. All of it is drawn
  from the app's own elements: `VolumeControl` hands the stylesheet its level as `--np-volume`
  and asks `skinLook` whether the slider is slid or turned (a knob follows an up or down drag),
  and the knob's number is the slider's own `aria-valuenow`. A control added to Now Playing is in
  the MH-1 with no skin work.
- Now Playing's look values: `SongDetailHost` takes its surface from `skinLook` (the desk in the
  app's own theme, where the plain app is forced dark on black), so `theme-color` stays the skin's
  there too. A music video behind it plays as a CRT (`VideoBackdrop`'s `data-mh-crt`); while it is
  up the overlay carries `data-mh-tv-on`, which gives it the skin's dark tokens in either theme so
  the ink reads over the picture, and the `theme-surface` store's `tv` claim turns `theme-color`
  to the skin's dark body.
- `HardwareVideo.svelte` — the MH-2: the bezel, plate, sync LCD and fullscreen key around Video
  mode's picture. It is a _housing_ for `VideoWatchTab` (its `housing` prop), which keeps the
  picture, its sync and fullscreen, renders the frame as the bezel's `display-screen`, and fits the
  picture into the stage less whatever the housing measures around it. A housing is the one shape
  skin markup may take: it wraps a snippet the component owns, and is handed nothing to re-render.
  Hand-built rather than SPEC-01's `Device`, which zooms itself to fit and would scale the
  fullscreen overlay inside it. Loaded on demand from `TrackPanel`, because its stylesheet, the
  vendored kit, is ~150 KB gzipped and nobody without the easter egg should download it.
- `behaviors/` — haptics, key sounds, keyboard-only focus rings and the block caret, loaded only once
  the skin is on.

## What is vendored, and from where

- `skin/` — SPEC-01's stylesheets, verbatim, from `registry/hardware/ui/hardware/` at commit
  `b8f91b6646d9280d9de8e0f5b2c6453cb4f237cb`. MIT, © Shu Ding (`./LICENSE`). Keep them verbatim so
  they can be refreshed; MusicHoarder's changes go in `musichoarder.css` and `app-skin.css`.
  They load with the MH-2, and stay in the document once loaded. Most of their rules are under
  `[data-skin="hardware"]`, but `devices.css` and `layout.css` also carry bare attribute selectors
  (`[data-row]`, `[data-section]`, `[data-art]`, `[data-kit="field"]`, …). Nothing outside the
  MH-2 uses those attributes; keep it that way, or the skin's layout reaches the app after the
  switch is turned off.
- `behaviors/` — SPEC-01's page-wide behaviours, same commit and licence, with local changes:
  - everything checks `skinActive()` (`active.ts`) first, because these listen at the document and
    cannot be uninstalled — switching the skin off must make the app feel exactly as before (the
    caret only follows a skin field, and the popup watcher only works while sounds are on);
  - the app's own buttons, switches, rows and tabs are added to the haptic and sound selectors;
  - sounds are **off unless turned on** (a second Web Audio context clicking over the music could
    disturb the iOS audio session that background playback relies on), and not offered at all in
    the app shell, whose web view shares the native player's session: a context there had WebKit
    set it to ambient, which stopped the music when the app left the foreground;
  - Safari's Tab-order patch is left out (it retabs every button on the page).
- `/static/fonts/spec-mono/` — SPEC-01's subset of IBM Plex Mono, OFL 1.1 (`OFL.txt` beside it).
  Self-hosted because the CSP's `font-src` is `'self'`.
- `Icon.svelte` — path data from IBM's Carbon icons, Apache-2.0 (`./CARBON-LICENSE`).

## What is ported

SPEC-01 ships React components only. The ones the MH-2 needs are ported to Svelte 5 on
`@ui-sdk/svelte`, keeping the `data-kit` attributes the skin styles: `Hardware` (the root), `Lcd`
and `ButtonGroup` (SDK `Button`, with `Icon`). When refreshing `skin/`, diff SPEC-01's React files
for these components too: a changed wrapper or `data-kit` name there must be followed here, or the
skin quietly misses the element.
