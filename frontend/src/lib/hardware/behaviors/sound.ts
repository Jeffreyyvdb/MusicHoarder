// The kit's interaction sounds, like a mechanical keyboard app: every sound
// is synthesized with the Web Audio API on the spot, so there are no audio
// files. Each play varies its pitch and level a little, as no two presses of
// a real switch sound alike.
//
// - Keys sound in two parts, as a switch does: a thock as the key goes down
//   (on the press, so nothing lags) and a lighter click as it comes back up
//   (on the click). A primary key is deeper. Latches are quieter: one soft
//   catch going down, where the key stays; going up, a light push and a
//   faint spring as it releases.
// - Detents, wheel rows and switch positions tick: short and bright.
// - Anything on a screen (a list row, a screen tab, an LCD) blips like a
//   piezo instead, since it's electronics rather than mechanics.
// - Typing in a text field sounds like a keyboard: a switch's click as each
//   key goes down and a lighter return as it comes up, deeper for the space
//   bar and heavier for Enter and Backspace; a held key's repeats only tick.
// - A popup opening (a menu, a list, a calendar, a context menu) flips open
//   with a soft pop, unless the press that opened it already clicked.
// - Outcomes chime: copied, done, or an error.
//
// Sound follows the haptics: `haptic(kind)` plays the matching sound, so
// every press, tick and drag that buzzes also clicks. It can be muted, and
// the choice is remembered.
import { isNativeShell } from "#lib/native/shell.ts"
import { skinActive } from "./active.ts"
import { type Haptic } from "./haptics.ts"

type Voice =
  | "down"
  | "down-primary"
  | "up"
  | "latch-down"
  | "latch-up"
  | "tick"
  | "blip"
  | "type"
  | "type-space"
  | "type-big"
  | "type-up"
  | "open"
  | "success"
  | "warning"
  | "error"

const storageKey = "spec01:sound"
// MusicHoarder: off unless turned on. A second Web Audio context clicking over the music could
// disturb the audio session iOS keeps for background playback (see player.svelte.ts).
let enabled = (() => {
  try {
    return typeof localStorage !== "undefined" && localStorage.getItem(storageKey) === "on"
  } catch {
    // Storage blocked: sounds stay off.
    return false
  }
})()
const listeners = new Set<(on: boolean) => void>()

function soundEnabled() {
  return enabled
}

function setSoundEnabled(on: boolean) {
  enabled = on
  try {
    localStorage.setItem(storageKey, on ? "on" : "off")
  } catch {
    // Storage blocked (private mode): the choice lasts this page only.
  }
  listeners.forEach((listener) => listener(on))
}

function onSoundChange(listener: (on: boolean) => void) {
  listeners.add(listener)
  return () => void listeners.delete(listener)
}

// One context for the page, made on the first sound: browsers only let a
// page start audio inside a user gesture, which every sound here follows.
let ctx: AudioContext | null = null
let master: GainNode | null = null
let noise: AudioBuffer | null = null

// MusicHoarder: in the app shell (mobile/) the music plays natively and the page never claims an
// audio session (player.svelte.ts skips it there), so iOS takes these clicks for ambient sound and
// the silent switch mutes them. The app's own session is `playback` already (AudioEngine.swift), so
// the page declares the same before it sounds, and the clicks play in silent mode as the music
// does. A browser is left alone: its player claims `playback` while music plays, and a silent
// phone that plays nothing stays silent, as the iOS keyboard does.
type AudioSessionNavigator = Navigator & { audioSession?: { type: string } }

function claimShellSession() {
  if (!isNativeShell()) return
  const session = (navigator as AudioSessionNavigator).audioSession
  if (!session || session.type === "playback") return
  try {
    session.type = "playback"
  } catch {
    // Refused by this engine: the clicks follow the silent switch, as before.
  }
}

function audio() {
  claimShellSession()
  // A context the browser closed is replaced.
  if (ctx?.state === "closed") ctx = null
  if (!ctx) {
    const Context = window.AudioContext ?? (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext
    if (!Context) return null
    ctx = new Context({ latencyHint: "interactive" })
    master = ctx.createGain()
    master.gain.value = 0.55
    master.connect(ctx.destination)
    // Half a second of white noise, the raw material of every click.
    noise = ctx.createBuffer(1, ctx.sampleRate / 2, ctx.sampleRate)
    const data = noise.getChannelData(0)
    for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1
  }
  // Not only "suspended": Safari parks a context as "interrupted" when the
  // tab goes to the background, the screen locks or other audio plays, and
  // never resumes it on its own, which left sounds missing until a reload.
  if (ctx.state !== "running") ctx.resume().catch(() => {})
  return ctx
}

// Browsers only let audio start inside a gesture, and iOS only on some
// (a touch's end, a click). So every gesture unlocks: it makes the context
// on the first, and resumes it whenever it's stopped, so sounds that follow
// outside a gesture, like a drag's ticks, still play.
function unlock() {
  if (enabled && typeof window !== "undefined" && skinActive()) audio()
}

const vary = (amount: number) => 1 + (Math.random() * 2 - 1) * amount

// A burst of filtered noise: the contact of plastic on plastic.
function burst(at: number, { type = "bandpass", freq, q = 1, level, decay }: { type?: BiquadFilterType; freq: number; q?: number; level: number; decay: number }) {
  const c = ctx!
  const source = c.createBufferSource()
  source.buffer = noise
  source.playbackRate.value = vary(0.1)
  const filter = c.createBiquadFilter()
  filter.type = type
  filter.frequency.value = freq * vary(0.06)
  filter.Q.value = q
  const gain = c.createGain()
  gain.gain.setValueAtTime(0, at)
  gain.gain.linearRampToValueAtTime(level * vary(0.15), at + 0.0008)
  gain.gain.exponentialRampToValueAtTime(0.0001, at + decay)
  source.connect(filter).connect(gain).connect(master!)
  source.start(at, Math.random() * 0.4)
  source.stop(at + decay + 0.02)
}

// A tone with a pitch drop and a fast decay: a key's body or a piezo beep.
function tone(at: number, { type = "sine", freq, to = freq, level, decay, attack = 0.001 }: { type?: OscillatorType; freq: number; to?: number; level: number; decay: number; attack?: number }) {
  const c = ctx!
  const osc = c.createOscillator()
  osc.type = type
  const f = freq * vary(0.04)
  osc.frequency.setValueAtTime(f, at)
  osc.frequency.exponentialRampToValueAtTime(Math.max(20, (to / freq) * f), at + decay)
  const gain = c.createGain()
  gain.gain.setValueAtTime(0, at)
  gain.gain.linearRampToValueAtTime(level * vary(0.12), at + attack)
  gain.gain.exponentialRampToValueAtTime(0.0001, at + decay)
  osc.connect(gain).connect(master!)
  osc.start(at)
  osc.stop(at + decay + 0.02)
}

const voices: Record<Voice, (at: number) => void> = {
  // A key bottoming out: a click on top of a short, falling body.
  down: (at) => {
    burst(at, { freq: 2600, q: 0.9, level: 0.32, decay: 0.022 })
    tone(at, { freq: 210, to: 130, level: 0.4, decay: 0.05 })
  },
  // A primary key is bigger and lower.
  "down-primary": (at) => {
    burst(at, { freq: 2100, q: 0.8, level: 0.36, decay: 0.026 })
    tone(at, { freq: 165, to: 100, level: 0.55, decay: 0.07 })
  },
  // The key returning: lighter and higher.
  up: (at) => {
    burst(at, { freq: 3900, q: 1.2, level: 0.16, decay: 0.012 })
    tone(at, { freq: 300, to: 240, level: 0.1, decay: 0.018 })
  },
  // A latch catching: a soft, low clack with a faint spring behind it.
  "latch-down": (at) => {
    burst(at, { freq: 2000, q: 0.8, level: 0.18, decay: 0.018 })
    tone(at, { freq: 160, to: 110, level: 0.2, decay: 0.045 })
    burst(at + 0.004, { freq: 1150, q: 14, level: 0.035, decay: 0.05 })
  },
  // A latch releasing: a light snap and a fainter, brighter ring.
  "latch-up": (at) => {
    burst(at, { freq: 3200, q: 1, level: 0.11, decay: 0.012 })
    burst(at + 0.003, { freq: 1600, q: 12, level: 0.03, decay: 0.04 })
  },
  // A detent: a tiny, bright click.
  tick: (at) => {
    burst(at, { type: "highpass", freq: 3200, q: 0.7, level: 0.2, decay: 0.006 })
    tone(at, { freq: 4300, level: 0.035, decay: 0.01 })
  },
  // A piezo beep: a soft square tone through a low-pass.
  blip: (at) => {
    tone(at, { type: "square", freq: 2350, level: 0.035, decay: 0.045, attack: 0.002 })
  },
  // A keyboard switch: crisper and shorter than a device key.
  type: (at) => {
    burst(at, { freq: 3300, q: 1.1, level: 0.22, decay: 0.014 })
    tone(at, { freq: 260, to: 190, level: 0.18, decay: 0.03 })
  },
  // The space bar: a longer, lower clack with a little rattle.
  "type-space": (at) => {
    burst(at, { freq: 2200, q: 0.8, level: 0.26, decay: 0.022 })
    tone(at, { freq: 170, to: 120, level: 0.26, decay: 0.05 })
    burst(at + 0.006, { freq: 900, q: 6, level: 0.05, decay: 0.03 })
  },
  // Enter and Backspace: bigger keys, a heavier thock.
  "type-big": (at) => {
    burst(at, { freq: 2600, q: 0.9, level: 0.26, decay: 0.018 })
    tone(at, { freq: 200, to: 140, level: 0.28, decay: 0.045 })
  },
  // A key coming back up.
  "type-up": (at) => {
    burst(at, { freq: 4400, q: 1.3, level: 0.08, decay: 0.008 })
  },
  // A popup flipping open: a light tap and a short rising note.
  open: (at) => {
    burst(at, { freq: 2600, q: 1, level: 0.1, decay: 0.012 })
    tone(at, { freq: 520, to: 820, level: 0.045, decay: 0.06, attack: 0.003 })
  },
  success: (at) => {
    tone(at, { freq: 1318, level: 0.07, decay: 0.09, attack: 0.004 })
    tone(at + 0.075, { freq: 1760, level: 0.07, decay: 0.16, attack: 0.004 })
  },
  warning: (at) => {
    tone(at, { type: "triangle", freq: 880, level: 0.08, decay: 0.08 })
    tone(at + 0.11, { type: "triangle", freq: 880, level: 0.08, decay: 0.08 })
  },
  error: (at) => {
    tone(at, { type: "square", freq: 220, level: 0.04, decay: 0.09 })
    tone(at + 0.12, { type: "square", freq: 196, level: 0.04, decay: 0.14 })
  },
}

let lastPlayed = 0
// MusicHoarder: a sound asked for while the context was not running, waiting for it to start.
let waiting = false
// How long a waiting sound still belongs to the press that asked for it.
const waitLimitMs = 400

function play(voice: Voice) {
  if (!enabled || typeof window === "undefined" || !skinActive()) return
  const c = audio()
  if (!c || !master) return
  lastPlayed = performance.now()
  if (c.state === "running") {
    voices[voice](c.currentTime + 0.002)
    return
  }
  // MusicHoarder: a context that is not running yet has a stopped clock. WebKit makes it on the
  // first press but starts it only at the touch's end (a press is not a gesture it lets audio start
  // from), and Safari parks it whenever the page is in the background. A sound scheduled on that
  // clock waits for the start and then plays together with everything queued behind it, so the
  // first tap's key-down and key-up came out late, as one. Instead, the first sound waits for the
  // start and plays then, if that is still soon after the press; the others asked meanwhile are
  // dropped.
  if (waiting) return
  waiting = true
  const asked = performance.now()
  c.resume().then(
    () => {
      waiting = false
      if (performance.now() - asked < waitLimitMs && c.state === "running") voices[voice](c.currentTime + 0.002)
    },
    () => {
      waiting = false
    },
  )
}

// Popups that make a sound as they open: their content parts, marked open
// by UI SDK with data-open. Hover cards and tooltips open on hover and stay
// silent.
const sounding = /-(content|popup|positioner)$/
const silent = /^(hover-card|tooltip)-/

function watchPopups() {
  // A popup can open two ways: added to the page already marked open, or
  // marked open in place. Each opening sounds once.
  const heard = new WeakSet<Element>()
  const opened = (el: Element) => {
    const slot = el.getAttribute("data-slot") ?? ""
    if (!el.hasAttribute("data-open") || !sounding.test(slot) || silent.test(slot) || heard.has(el)) return false
    heard.add(el)
    return true
  }
  new MutationObserver((records) => {
    // MusicHoarder: nothing to sound while muted (the default) or with the skin off.
    if (!enabled || !skinActive()) return
    let open = false
    for (const record of records) {
      if (record.type === "attributes") {
        const el = record.target as Element
        // Closing clears the mark, so the next opening sounds again.
        if (!el.hasAttribute("data-open")) heard.delete(el)
        else if (opened(el)) open = true
      } else {
        for (const node of record.addedNodes) {
          if (!(node instanceof Element)) continue
          if (opened(node)) open = true
          for (const el of node.querySelectorAll("[data-open][data-slot]")) if (opened(el)) open = true
        }
      }
    }
    // The press that opened it may have clicked already.
    if (open && performance.now() - lastPlayed >= 150) play("open")
  }).observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ["data-open"] })
}

// Where the interaction is happening: the pressed element, or the focused
// one for the keyboard. Selections on a screen blip instead of ticking.
const screen = '[data-kit="item-list"], [data-kit="lcd"], [data-kit="date-lcd"], [data-kit="hover-lcd"], [data-kit="display-screen"]'
let pointerTarget: Element | null = null
let pointerAt = 0
// The key whose down-stroke already sounded, so its click plays the
// up-stroke rather than a second thock.
let downKey: Element | null = null

// MusicHoarder: plus the app's own buttons and switches (see haptics.ts).
const keyLike = '[data-kit="button"], [data-kit="code-copy"], [data-kit="check"], [data-kit="checkbox"], [data-kit="dpad-zone"], [data-kit="rocker-part"], [data-kit="jog-center"], [data-slot="button"], [data-slot="filter-chip"], [data-slot="switch"]'

function context() {
  return performance.now() - pointerAt < 1500 ? pointerTarget : document.activeElement
}

// The sound for a haptic, as `haptic()` plays it.
function soundFor(kind: Haptic) {
  const el = context()
  switch (kind) {
    case "selection":
      return play(el?.closest(screen) ? "blip" : "tick")
    case "light":
    case "medium":
    case "heavy": {
      const key = el?.closest(keyLike)
      if (key && key === downKey) {
        downKey = null
        return play("up")
      }
      return play(kind === "light" ? "down" : "down-primary")
    }
    case "rigid":
    case "soft": {
      const key = el?.closest(keyLike)
      if (key && key === downKey) {
        downKey = null
        // Latching, the press already caught and the key stays down, so
        // there's nothing more; releasing, its spring lets go now.
        return kind === "rigid" ? undefined : play("latch-up")
      }
      return play(kind === "rigid" ? "latch-down" : "latch-up")
    }
    default:
      return play(kind)
  }
}

// Text fields that sound like a keyboard as you type in them.
const typing = 'textarea, [contenteditable="true"], input:not([type]), input[type="text"], input[type="search"], input[type="email"], input[type="url"], input[type="tel"], input[type="password"], input[type="number"]'

function keyVoice(key: string): Voice | null {
  if (key === " ") return "type-space"
  if (key === "Enter" || key === "Backspace" || key === "Delete" || key === "Tab") return "type-big"
  // Printable keys, and the virtual keyboards that report none.
  if (key.length === 1 || key === "Unidentified" || key === "Process") return "type"
  return null
}

// The down-stroke, on the press itself.
function installSound() {
  for (const type of ["pointerdown", "pointerup", "touchend", "keydown", "click"])
    document.addEventListener(type, unlock, { capture: true, passive: true })
  // Back from another tab or app: try to resume straight away (the next
  // gesture will if the browser doesn't allow it yet).
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible" && ctx && ctx.state !== "running") ctx.resume().catch(() => {})
  })
  watchPopups()
  document.addEventListener(
    "keydown",
    (event) => {
      if (event.isComposing || event.metaKey || event.ctrlKey || event.altKey) return
      if (!(event.target as Element | null)?.closest?.(typing)) return
      const voice = keyVoice(event.key)
      if (!voice) return
      // A held key auto-repeating ticks softly instead of clacking.
      play(event.repeat ? "tick" : voice)
    },
    true,
  )
  document.addEventListener(
    "keyup",
    (event) => {
      if (event.isComposing || event.metaKey || event.ctrlKey || event.altKey) return
      if (!(event.target as Element | null)?.closest?.(typing)) return
      if (keyVoice(event.key)) play("type-up")
    },
    true,
  )
  document.addEventListener(
    "pointerdown",
    (event) => {
      pointerTarget = event.target as Element
      pointerAt = performance.now()
      if (event.button !== 0) return
      const key = (event.target as Element | null)?.closest?.(keyLike)
      if (!key || key.matches(':disabled, [aria-disabled="true"], [data-disabled]')) return
      downKey = key
      const latch = key.matches('[data-kit="check"], [data-kit="checkbox"]') || key.querySelector('[data-kit="checkbox"]')
      if (latch) {
        const box = key.matches('[data-kit="checkbox"]') ? key : key.querySelector('[data-kit="checkbox"]')
        // Going down catches; coming up, the press is just a light push.
        play(box?.hasAttribute("data-checked") ? "up" : "latch-down")
      } else {
        play(key.matches('[data-tone="primary"]') ? "down-primary" : "down")
      }
    },
    true,
  )
}

export { installSound, onSoundChange, setSoundEnabled, soundEnabled, soundFor }
