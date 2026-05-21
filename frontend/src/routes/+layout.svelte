<script lang="ts">
  import '../app.css';
  import type { Snippet } from 'svelte';
  import { ModeWatcher } from 'mode-watcher';
  import { Toaster } from '$lib/components/ui/sonner';
  import Analytics from '$lib/components/Analytics.svelte';

  type Props = { children: Snippet };
  const { children }: Props = $props();
</script>

<ModeWatcher defaultMode="system" />
<Analytics />
<Toaster position="top-center" richColors closeButton />

{@render children()}
