<script lang="ts">
  import '../app.css';
  import type { Snippet } from 'svelte';
  import { ModeWatcher } from 'mode-watcher';
  import { beforeNavigate } from '$app/navigation';
  import { updated } from '$app/state';
  import { Toaster } from '$lib/components/ui/sonner';
  import Analytics from '$lib/components/Analytics.svelte';

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
</script>

<ModeWatcher defaultMode="system" />
<Analytics />
<Toaster position="top-center" richColors closeButton />

{@render children()}
