<script lang="ts">
  import { Check, Copy } from '@lucide/svelte';

  type Props = {
    /** The exact text copied to the clipboard (and rendered, unless `children` overrides). */
    text: string;
    /** Window-chrome label, e.g. "your-server : ~" or "docker-compose.yml". */
    label?: string;
    class?: string;
  };
  const { text, label = 'your-server : ~', class: className = '' }: Props = $props();

  let copied = $state(false);
  let copyFailed = $state(false);
  let timer: ReturnType<typeof setTimeout> | undefined;

  function copy() {
    // Clipboard is browser-only; the button is inert during SSR until hydrated.
    navigator.clipboard
      ?.writeText(text)
      .then(flash)
      .catch(fail);
  }
  function flash() {
    copyFailed = false;
    copied = true;
    clearTimeout(timer);
    timer = setTimeout(() => (copied = false), 1800);
  }
  function fail() {
    copied = false;
    copyFailed = true;
    clearTimeout(timer);
    timer = setTimeout(() => (copyFailed = false), 1800);
  }
</script>

<div
  class="bg-surface-sunken border-border overflow-hidden rounded-[10px] border {className}"
  style="box-shadow: 0 8px 24px rgba(0,0,0,0.08), 0 0 0 0.5px rgba(0,0,0,0.06);"
>
  <div class="bg-card border-border flex items-center gap-2 border-b px-3 py-2.5">
    <span class="flex gap-1.5">
      <span class="block size-[9px] rounded-full" style="background:#ff5f57"></span>
      <span class="block size-[9px] rounded-full" style="background:#febc2e"></span>
      <span class="block size-[9px] rounded-full" style="background:#28c840"></span>
    </span>
    <span class="text-muted-foreground ml-1 font-mono text-[10.5px]">{label}</span>
    <button
      type="button"
      onclick={copy}
      class="text-primary hover:bg-accent ml-auto inline-flex items-center gap-1.5 rounded-md px-2 py-1 text-[11.5px] font-medium transition-colors"
    >
      {#if copied}
        <Check class="size-3" /> Copied
      {:else if copyFailed}
        <Copy class="size-3" /> Failed
      {:else}
        <Copy class="size-3" /> Copy
      {/if}
    </button>
  </div>
  <pre
    class="text-muted-foreground overflow-x-auto px-4 py-3.5 font-mono text-[12.5px] leading-[1.7]">{text}</pre>
</div>
