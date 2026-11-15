<script lang="ts">
  // A device's body: one molded off-white shell with its model printed small in the corner beside a
  // power LED. Where the page is narrower than the device, as on a phone, the whole device is zoomed
  // down to fit rather than reflowed, so its layout stays as designed.
  import type { Snippet } from 'svelte';
  import './skin/devices.css';

  let {
    model,
    name,
    on = true,
    children
  }: { model: string; name: string; on?: boolean; children: Snippet } = $props();

  let body: HTMLElement;

  $effect(() => {
    const el = body;
    const page = el.parentElement;
    if (!page) return;
    el.style.maxWidth = 'none';
    let width = -1;
    const fit = () => {
      // Only a change of the page's width refits; zooming changes its height.
      if (page.clientWidth === width) return;
      width = page.clientWidth;
      el.style.zoom = '';
      const style = getComputedStyle(page);
      const room = width - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
      if (room > 0 && el.offsetWidth > room) el.style.zoom = String(room / el.offsetWidth);
    };
    // Fitted now, not in the observer's first callback: zooming there would resize the page it is
    // watching before the callback returns, and deferring that to a frame would show the device
    // unzoomed (overflowing a phone) for one paint.
    // eslint-disable-next-line local/no-layout-read-before-resize-observer -- the first paint must already be zoomed to fit
    fit();
    // Refits wait a frame: zooming inside the observer's callback would resize the page it's
    // watching before the callback returns.
    let frame = 0;
    const observer = new ResizeObserver(() => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(fit);
    });
    observer.observe(page);
    return () => {
      observer.disconnect();
      cancelAnimationFrame(frame);
    };
  });
</script>

<section bind:this={body} data-device aria-label={name}>
  <header data-device-plate>
    <span data-device-power data-on={on || undefined} aria-hidden="true"></span>
    <strong>{model}</strong>
    <span>{name}</span>
  </header>
  {@render children()}
</section>
