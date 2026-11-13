<script lang="ts">
  import { cn } from '#lib/utils.ts';

  /**
   * A thin determinate progress bar on the native `<progress>` element, which carries the
   * progressbar role, its value and its range to assistive tech by itself. The track is the
   * translucent `--muted` fill and the bar is the tint (or amber for a paused job). Give it an
   * accessible name with `label`.
   */
  type Props = {
    value: number;
    /** Defaults to 1, so `value` can be a fraction. */
    max?: number;
    label: string;
    tone?: 'primary' | 'warning';
    class?: string;
  };
  const { value, max = 1, label, tone = 'primary', class: className = '' }: Props = $props();

  // A max of 0 (nothing to do yet) is not a valid range; show an empty bar instead.
  const range = $derived(max > 0 ? max : 1);
  const clamped = $derived(Math.min(Math.max(max > 0 ? value : 0, 0), range));
</script>

<progress
  class={cn('mh-progress', tone === 'warning' && 'mh-progress-warning', className)}
  value={clamped}
  max={range}
  aria-label={label}
></progress>

<style>
  .mh-progress {
    appearance: none;
    display: block;
    inline-size: 100%;
    block-size: 0.25rem;
    border: 0;
    border-radius: 9999px;
    overflow: hidden;
    background-color: var(--muted);
    color: var(--primary);
  }
  .mh-progress-warning {
    color: var(--warning);
  }
  .mh-progress::-webkit-progress-bar {
    background-color: var(--muted);
  }
  .mh-progress::-webkit-progress-value {
    background-color: currentColor;
    transition: inline-size 300ms ease-out;
  }
  .mh-progress::-moz-progress-bar {
    background-color: currentColor;
  }
</style>
