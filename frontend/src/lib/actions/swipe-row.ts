import { EDGE } from '#lib/actions/edge-swipe-back.ts';
import { EASE_OUT, prefersReducedMotion } from '#lib/motion.ts';

/**
 * `use:swipeRow` — swipe a list row sideways to run one action, the way Spotify adds a song to the
 * queue (swipe right) and iOS lists delete (swipe left).
 *
 * Touch and pen only; a mouse never swipes. The row's content follows the finger and a coloured
 * panel opens behind it on the side it came from, carrying the action's icon. Past the commit
 * distance the icon grows (and the haptic engine ticks where there is one); letting go there runs
 * `onswipe`. `leading` swipes spring back with a check in the panel, since the row stays where it
 * is; `trailing` ones (a removal) slide the row out first, since it is about to go.
 *
 * A swipe is only ever a shortcut: every row that has one keeps a visible twin (a menu item, a
 * button), because a gesture is never discoverable alone and a screen reader cannot perform it.
 *
 * The gesture never fights the page: the row takes `touch-action: pan-y pinch-zoom`, so a vertical
 * drag scrolls the list as before and only a sideways one is ours, and a touch that starts in the
 * left-edge strip is left to the installed app's edge swipe back (`edgeSwipeBack`). It swallows the
 * click a swipe's lift would otherwise deliver, so a swipe never also plays the row.
 *
 * Nothing is added to the row until it is first swiped: lists build one of these per row.
 */

export type SwipeRowParams = {
  /** `leading`: swipe right, the panel opens on the left. `trailing`: swipe left. */
  side: 'leading' | 'trailing';
  /** The panel's fill: the app's accent, or destructive red. */
  tone: 'primary' | 'destructive';
  /** The panel's icon, as an inline SVG string (aria-hidden; the action is announced elsewhere). */
  icon: string;
  onswipe: () => void;
  /** Off: no gesture and no styles. */
  disabled?: boolean;
};

/** Movement before the gesture decides whether it is sideways (ours) or a scroll. */
const SLOP = 10;
/** The commit distance: a quarter of the row, never less than this. */
const MIN_COMMIT = 80;
const COMMIT_FRACTION = 0.25;
/** Past the commit distance the row moves at this share of the finger's travel. */
const RESISTANCE = 0.35;
const SETTLE_MS = 220;
/** How long the check rests in a committed leading panel before the row springs back. */
const CONFIRM_MS = 280;
const CLICK_SWALLOW_MS = 600;

const CHECK_SVG =
  '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M20 6 9 17l-5-5"/></svg>';

type Gesture = {
  id: number;
  x0: number;
  y0: number;
  decided: boolean;
  dx: number;
  width: number;
  armed: boolean;
};

export function swipeRow(
  node: HTMLElement,
  params: SwipeRowParams
): { update(params: SwipeRowParams): void; destroy(): void } {
  let options = params;
  let gesture: Gesture | null = null;
  let panel: HTMLElement | null = null;
  let iconEl: HTMLElement | null = null;
  let settleTimer: ReturnType<typeof setTimeout> | null = null;
  let swallowClick = false;
  let swallowTimer: ReturnType<typeof setTimeout> | null = null;
  let savedTouchAction = '';
  let styled = false;

  const sign = () => (options.side === 'leading' ? 1 : -1);

  function applyStyles() {
    if (styled || options.disabled) return;
    styled = true;
    savedTouchAction = node.style.touchAction;
    node.style.touchAction = 'pan-y pinch-zoom';
  }

  function restoreStyles() {
    if (!styled) return;
    styled = false;
    node.style.touchAction = savedTouchAction;
  }

  /** The row's own children: what slides. The panel stays put behind them. */
  function content(): HTMLElement[] {
    return Array.from(node.children).filter(
      (child): child is HTMLElement => child instanceof HTMLElement && child !== panel
    );
  }

  function ensurePanel(): HTMLElement {
    if (panel) return panel;
    // The row must clip the panel and anchor it; most rows already are positioned.
    if (getComputedStyle(node).position === 'static') node.style.position = 'relative';
    node.style.overflow = 'hidden';
    panel = document.createElement('div');
    panel.setAttribute('aria-hidden', 'true');
    panel.dataset.swipePanel = '';
    Object.assign(panel.style, {
      position: 'absolute',
      top: '0',
      bottom: '0',
      width: '0px',
      display: 'flex',
      alignItems: 'center',
      overflow: 'hidden',
      pointerEvents: 'none',
      color: 'white',
      zIndex: '0'
    });
    iconEl = document.createElement('span');
    Object.assign(iconEl.style, {
      display: 'grid',
      placeItems: 'center',
      flexShrink: '0',
      width: '72px',
      transition: `transform 150ms ${EASE_OUT}`
    });
    panel.append(iconEl);
    node.prepend(panel);
    return panel;
  }

  function dressPanel() {
    const el = ensurePanel();
    const leading = options.side === 'leading';
    el.style.left = leading ? '0' : '';
    el.style.right = leading ? '' : '0';
    el.style.justifyContent = leading ? 'flex-start' : 'flex-end';
    el.style.background = options.tone === 'destructive' ? 'var(--destructive)' : 'var(--primary)';
    el.style.color = options.tone === 'destructive' ? 'white' : 'var(--primary-foreground)';
    if (iconEl) {
      iconEl.innerHTML = options.icon;
      iconEl.style.transform = 'scale(0.85)';
    }
  }

  function setOffset(px: number, transition: string) {
    for (const child of content()) {
      child.style.transition = transition;
      child.style.transform = px === 0 ? '' : `translateX(${px}px)`;
    }
    if (panel) {
      panel.style.transition = transition ? transition.replaceAll('transform', 'width') : '';
      panel.style.width = `${Math.abs(px)}px`;
    }
  }

  function commitDistance(width: number) {
    return Math.max(MIN_COMMIT, width * COMMIT_FRACTION);
  }

  /** The finger's travel, as how far the row shows it: 1:1 to the commit point, then resisted. */
  function shown(travel: number, width: number) {
    const commit = commitDistance(width);
    const t = Math.max(0, travel);
    return t <= commit ? t : commit + (t - commit) * RESISTANCE;
  }

  function tick() {
    try {
      navigator.vibrate?.(10);
    } catch {
      // without a user activation some browsers throw; the swipe matters, not the tick
    }
  }

  function settle(after?: () => void) {
    const reduced = prefersReducedMotion();
    const transition = reduced ? '' : `transform ${SETTLE_MS}ms ${EASE_OUT}`;
    setOffset(0, transition);
    if (settleTimer) clearTimeout(settleTimer);
    settleTimer = setTimeout(
      () => {
        settleTimer = null;
        for (const child of content()) child.style.transition = '';
        if (panel) panel.style.transition = '';
        after?.();
      },
      reduced ? 0 : SETTLE_MS
    );
  }

  function commit(g: Gesture) {
    swallowClick = true;
    if (options.side === 'leading') {
      // The row stays: show that it worked, then spring back.
      if (iconEl) {
        iconEl.innerHTML = CHECK_SVG;
        iconEl.style.transform = 'scale(1)';
      }
      options.onswipe();
      if (settleTimer) clearTimeout(settleTimer);
      settleTimer = setTimeout(() => settle(), prefersReducedMotion() ? 0 : CONFIRM_MS);
      return;
    }
    // The row is about to go: slide it the rest of the way out, then let the list remove it. The
    // offsets are cleared before that, since a keyed list may reuse this element for the next row.
    const reduced = prefersReducedMotion();
    if (reduced) {
      setOffset(0, '');
      options.onswipe();
      return;
    }
    setOffset(-g.width, `transform ${SETTLE_MS}ms ${EASE_OUT}`);
    if (settleTimer) clearTimeout(settleTimer);
    settleTimer = setTimeout(() => {
      settleTimer = null;
      setOffset(0, '');
      options.onswipe();
    }, SETTLE_MS);
  }

  function onPointerDown(e: PointerEvent) {
    if (options.disabled || e.pointerType === 'mouse' || !e.isPrimary || gesture) return;
    if (settleTimer) return; // still settling from the last one
    // The installed app's edge swipe back owns a touch that starts at the left edge.
    if (options.side === 'leading' && e.clientX <= EDGE) return;
    swallowClick = false;
    gesture = {
      id: e.pointerId,
      x0: e.clientX,
      y0: e.clientY,
      decided: false,
      dx: 0,
      width: node.getBoundingClientRect().width,
      armed: false
    };
  }

  function onPointerMove(e: PointerEvent) {
    const g = gesture;
    if (!g || e.pointerId !== g.id) return;
    const dx = e.clientX - g.x0;
    const dy = e.clientY - g.y0;
    if (!g.decided) {
      if (Math.hypot(dx, dy) < SLOP) return;
      // Ours only when it moves sideways, the right way, before it moves down.
      if (Math.abs(dx) <= Math.abs(dy) || Math.sign(dx) !== sign()) {
        gesture = null;
        return;
      }
      g.decided = true;
      try {
        node.setPointerCapture(g.id);
      } catch {
        // the pointer may already be gone
      }
      dressPanel();
    }
    g.dx = dx;
    const travel = dx * sign();
    const armed = travel >= commitDistance(g.width);
    if (armed !== g.armed) {
      g.armed = armed;
      if (iconEl) iconEl.style.transform = armed ? 'scale(1.15)' : 'scale(0.85)';
      if (armed) tick();
    }
    setOffset(shown(travel, g.width) * sign(), '');
  }

  function onPointerEnd(e: PointerEvent) {
    const g = gesture;
    if (!g || e.pointerId !== g.id) return;
    gesture = null;
    if (!g.decided) return;
    swallowClick = true;
    armSwallowExpiry();
    if (g.armed && e.type === 'pointerup') commit(g);
    else settle();
  }

  function armSwallowExpiry() {
    if (swallowTimer) clearTimeout(swallowTimer);
    swallowTimer = setTimeout(() => {
      swallowClick = false;
      swallowTimer = null;
    }, CLICK_SWALLOW_MS);
  }

  function onClick(e: MouseEvent) {
    if (!swallowClick) return;
    swallowClick = false;
    e.preventDefault();
    e.stopImmediatePropagation();
  }

  node.addEventListener('pointerdown', onPointerDown);
  node.addEventListener('pointermove', onPointerMove);
  node.addEventListener('pointerup', onPointerEnd);
  node.addEventListener('pointercancel', onPointerEnd);
  node.addEventListener('click', onClick, true);
  applyStyles();

  return {
    update(next) {
      options = next;
      if (next.disabled) {
        gesture = null;
        restoreStyles();
      } else {
        applyStyles();
      }
    },
    destroy() {
      if (settleTimer) clearTimeout(settleTimer);
      if (swallowTimer) clearTimeout(swallowTimer);
      node.removeEventListener('pointerdown', onPointerDown);
      node.removeEventListener('pointermove', onPointerMove);
      node.removeEventListener('pointerup', onPointerEnd);
      node.removeEventListener('pointercancel', onPointerEnd);
      node.removeEventListener('click', onClick, true);
      restoreStyles();
    }
  };
}
