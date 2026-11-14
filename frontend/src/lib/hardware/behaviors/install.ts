import { installBlockCaret } from "./block-caret.ts"
import { installHaptics } from "./haptics.ts"
import { installInputModality } from "./input-modality.ts"
import { installSound } from "./sound.ts"

// MusicHoarder: Safari's Tab-order patch is left out; it retabs every button on the page, not
// just the skin's.
//
// The page-wide behaviors the skin relies on: the block caret, haptics and
// sounds and keyboard-only focus rings. Each listens at
// the document, so they're installed once, however many roots there are.
let installed = false

function installHardware() {
  if (installed || typeof document === "undefined") return
  installed = true
  installBlockCaret()
  installHaptics()
  installSound()
  installInputModality()
}

export { installHardware }
