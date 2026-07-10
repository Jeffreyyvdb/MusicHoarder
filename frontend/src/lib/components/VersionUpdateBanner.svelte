<script lang="ts">
  import { onMount } from 'svelte';
  import { X } from '@lucide/svelte';
  import { fetchLatestVersion, type LatestVersionInfo } from '$lib/api-client';

  // Shell banner policy: the AppShellV2 suppresses this notice while a
  // higher-priority banner (library offline / grading error) is visible, so at
  // most one banner renders at a time.
  type Props = { suppressed?: boolean };
  const { suppressed = false }: Props = $props();

  let info = $state<LatestVersionInfo | null>(null);
  let dismissedVersion = $state<string | null>(null);

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

  onMount(() => {
    void refresh();
    // The backend caches the GitHub result, so a re-check on tab focus is cheap and catches a release
    // that landed while the tab sat in the background. No interval polling needed.
    const onFocus = () => void refresh();
    window.addEventListener('focus', onFocus);
    return () => window.removeEventListener('focus', onFocus);
  });
</script>

<!--
  One slim line, no tinted wall: a small accent dot + sentence, separated from
  content by the border-b hairline only. Update instructions (compose command,
  Arcane note) live in Settings → Updates, which "How to update" links to.
-->
{#if show && info && !suppressed}
  <div
    aria-live="polite"
    class="border-border/70 text-muted-foreground flex h-9 shrink-0 items-center gap-2.5 border-b px-4 text-[13px] sm:px-7"
  >
    <span class="bg-primary size-1.5 shrink-0 rounded-full" aria-hidden="true"></span>
    <p class="min-w-0 flex-1 truncate">
      <span class="text-foreground font-medium">MusicHoarder {info.latest} available</span>
      {#if info.releaseUrl}
        <span class="mx-1 hidden sm:inline" aria-hidden="true">—</span>
        <a
          href={info.releaseUrl}
          target="_blank"
          rel="noopener noreferrer"
          class="hover:text-foreground hidden underline underline-offset-3 transition-colors sm:inline"
        >View release notes</a>
      {/if}
      <span class="mx-1" aria-hidden="true">·</span>
      <a href="/settings#updates" class="hover:text-foreground underline underline-offset-3 transition-colors"
      >How to update</a>
    </p>
    <button
      type="button"
      onclick={dismiss}
      aria-label="Dismiss update notice"
      class="hover:bg-muted hover:text-foreground focus-visible:ring-ring/60 -mr-1 grid size-6 shrink-0 place-items-center rounded-md outline-none transition-colors focus-visible:ring-2"
    >
      <X class="size-3.5" />
    </button>
  </div>
{/if}
