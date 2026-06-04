<script lang="ts">
  import '../app.css';
  import type { Snippet } from 'svelte';
  import { ModeWatcher } from 'mode-watcher';
  import { onMount } from 'svelte';
  import { afterNavigate, beforeNavigate } from '$app/navigation';
  import { updated } from '$app/state';
  import { Toaster } from '$lib/components/ui/sonner';
  import Analytics from '$lib/components/Analytics.svelte';
  import { clearStaleChunkRecovery } from '$lib/stale-chunk-recovery';
  import { installBottomInsetTracker } from '$lib/hooks/viewport-insets.svelte';

  type Props = { children: Snippet };
  const { children }: Props = $props();

  // A new version is live (detected via version polling). Do a full-page
  // navigation instead of client-side routing so we fetch the fresh build's
  // chunks rather than the deleted ones this tab still references.
  beforeNavigate((nav) => {
    if (updated.current && nav.to?.url && !nav.willUnload) {
      nav.cancel();
      location.href = nav.to.url.href;
    }
  });

  // A navigation completed without a stale-chunk failure, so chunks are loading
  // fine — reset the stale-chunk reload budget for the next deploy.
  afterNavigate(() => clearStaleChunkRecovery());

  // Track the browser's bottom chrome (e.g. Chrome Android's bottom address bar)
  // so the floating bottom nav / mini-player never hide behind it.
  onMount(() => installBottomInsetTracker());
</script>

<ModeWatcher defaultMode="system" />
<Analytics />
<Toaster position="top-center" richColors closeButton />

{@render children()}
