<script lang="ts">
  import { onMount } from 'svelte';
  import { Sparkles, ChevronDown, Copy, ExternalLink, X } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import { fetchLatestVersion, type LatestVersionInfo } from '$lib/api-client';
  import * as Alert from '$lib/components/ui/alert/index.js';
  import { Button } from '$lib/components/ui/button';

  const UPDATE_CMD = 'docker compose pull && docker compose up -d';

  let info = $state<LatestVersionInfo | null>(null);
  let dismissedVersion = $state<string | null>(null);
  let expanded = $state(false);

  const dismissKey = (version: string) => `mh:update-dismissed:${version}`;

  // Hidden unless the backend reports a strictly-newer release the user hasn't dismissed. A failed
  // fetch leaves `info` null → nothing shows (same graceful-degrade as the other banners).
  const show = $derived(
    info?.updateAvailable === true &&
      info.latest !== null &&
      dismissedVersion !== info.latest
  );

  async function refresh(): Promise<void> {
    try {
      const next = await fetchLatestVersion();
      info = next;
      if (next.latest) {
        try {
          dismissedVersion = localStorage.getItem(dismissKey(next.latest));
        } catch {
          dismissedVersion = null;
        }
      }
    } catch {
      info = null;
    }
  }

  function dismiss(): void {
    if (!info?.latest) return;
    dismissedVersion = info.latest;
    try {
      localStorage.setItem(dismissKey(info.latest), info.latest);
    } catch {
      // localStorage unavailable (private mode) — the in-memory flag still hides it this session.
    }
  }

  async function copyCommand(): Promise<void> {
    try {
      await navigator.clipboard.writeText(UPDATE_CMD);
      toast.success('Update command copied');
    } catch {
      toast.error('Could not copy — copy it manually');
    }
  }

  onMount(() => {
    void refresh();
    // The backend caches the GitHub result, so a re-check on tab focus is cheap and catches a release
    // that landed while the tab sat in the background. No interval polling needed.
    const onFocus = () => void refresh();
    window.addEventListener('focus', onFocus);
    return () => window.removeEventListener('focus', onFocus);
  });
</script>

{#if show && info}
  <Alert.Root
    aria-live="polite"
    class="mx-4 my-2 border-primary/40 bg-primary/10"
  >
    <Sparkles class="size-4" />
    <Alert.Title>MusicHoarder {info.latest} is available</Alert.Title>
    <Alert.Description class="flex flex-col gap-2">
      <span class="text-muted-foreground">You're running {info.current}.</span>
      <div class="flex flex-wrap items-center gap-3">
        {#if info.releaseUrl}
          <a
            href={info.releaseUrl}
            target="_blank"
            rel="noopener noreferrer"
            class="inline-flex items-center gap-1 font-medium underline"
          >
            View release notes <ExternalLink class="size-3.5" />
          </a>
        {/if}
        <button
          type="button"
          class="inline-flex items-center gap-1 font-medium underline"
          onclick={() => (expanded = !expanded)}
          aria-expanded={expanded}
        >
          How to update
          <ChevronDown class="size-3.5 transition-transform {expanded ? 'rotate-180' : ''}" />
        </button>
      </div>

      {#if expanded}
        <div class="mt-1 flex flex-col gap-2 rounded-md border bg-background/60 p-3">
          <div class="flex items-center justify-between gap-2">
            <code class="min-w-0 break-all text-xs">{UPDATE_CMD}</code>
            <Button variant="ghost" size="icon-sm" onclick={copyCommand} aria-label="Copy update command">
              <Copy class="size-3.5" />
            </Button>
          </div>
          <p class="text-muted-foreground text-xs">
            Run this in your compose stack's directory to pull the new images and recreate the
            containers. Or enable Arcane's <code class="text-xs">arcane.stack.auto-update</code> label
            to let Arcane pull and redeploy automatically.
          </p>
        </div>
      {/if}
    </Alert.Description>
    <Alert.Action>
      <Button variant="ghost" size="icon-sm" onclick={dismiss} aria-label="Dismiss update notice">
        <X class="size-4" />
      </Button>
    </Alert.Action>
  </Alert.Root>
{/if}
