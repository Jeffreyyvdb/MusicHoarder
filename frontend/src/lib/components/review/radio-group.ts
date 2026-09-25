/**
 * Keyboard behaviour for a single-choice list drawn as rows (`role="radiogroup"` around
 * `role="radio"` buttons): the candidate picker, the canonical-spelling picker, the bulk-approve
 * thresholds. A native radio group is one Tab stop whose arrow keys move AND select; rows styled
 * as iOS checkmark lists have to rebuild that by hand, or VoiceOver announces "toggle button"
 * with no "2 of 4" and keyboard users tab through every option.
 *
 * The rows own their roving tabindex (0 on the checked row, or the first when none is), so a
 * re-render after a selection keeps it right; this action only moves focus and selects.
 */

/** Where an arrow / Home / End key moves within `count` options from `index`; null: not ours. */
export function radioStep(key: string, index: number, count: number): number | null {
  if (count <= 0) return null;
  switch (key) {
    case 'ArrowDown':
    case 'ArrowRight':
      return (index + 1) % count;
    case 'ArrowUp':
    case 'ArrowLeft':
      return (index - 1 + count) % count;
    case 'Home':
      return 0;
    case 'End':
      return count - 1;
    default:
      return null;
  }
}

/** The tabindex of option `index` in a roving group whose checked option is `checked` (−1: none). */
export function radioTabIndex(index: number, checked: number): 0 | -1 {
  return index === (checked < 0 ? 0 : checked) ? 0 : -1;
}

export function radioGroup(node: HTMLElement) {
  function onkeydown(e: KeyboardEvent) {
    if (e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
    const radios = [...node.querySelectorAll<HTMLElement>('[role="radio"]')].filter(
      (r) => !r.hasAttribute('disabled') && r.getAttribute('aria-disabled') !== 'true'
    );
    const index = radios.indexOf(document.activeElement as HTMLElement);
    if (index < 0) return;
    const next = radioStep(e.key, index, radios.length);
    if (next == null) return;
    // Handled here: page-level shortcuts (Tag review's arrow keys) must not also move.
    e.preventDefault();
    e.stopPropagation();
    radios[next].focus();
    radios[next].click();
  }
  node.addEventListener('keydown', onkeydown);
  return {
    destroy() {
      node.removeEventListener('keydown', onkeydown);
    }
  };
}
