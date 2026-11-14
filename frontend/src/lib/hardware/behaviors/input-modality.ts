// Text fields match :focus-visible even when clicked, so the page tracks how
// focus last moved, as <html data-modality>: a press means pointer, and Tab or
// a navigation key outside a text field means keyboard. Typing into a field
// doesn't switch it. The skin shows some focus rings for keyboard only.
const navigationKeys = new Set(["Tab", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Home", "End", "PageUp", "PageDown"])

function installInputModality() {
  document.addEventListener("pointerdown", () => (document.documentElement.dataset.modality = "pointer"), true)
  document.addEventListener(
    "keydown",
    (event) => {
      const typing = (event.target as HTMLElement).matches?.("input, textarea, [contenteditable]")
      if (event.key === "Tab" || (navigationKeys.has(event.key) && !typing)) document.documentElement.dataset.modality = "keyboard"
    },
    true,
  )
}

export { installInputModality }
