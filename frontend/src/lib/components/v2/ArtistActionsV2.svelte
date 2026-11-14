<script lang="ts">
  import { Ellipsis, Play, Shuffle } from '@lucide/svelte';
  import { Button } from '#lib/components/ui/button/index.ts';
  import * as DropdownMenu from '#lib/components/ui/dropdown-menu/index.ts';
  import { Spinner } from '#lib/components/ui/spinner/index.ts';
  import type { ArtistReleaseKind } from '#lib/api-client.ts';
  import {
    ALL_KINDS,
    followSummary,
    KIND_PLURAL,
    toggleReleaseKind
  } from '#lib/artist-discography.ts';
  import type { ArtistPageState } from '#lib/stores/artist-page.svelte.ts';

  type Props = {
    /** The catalog side, for an admin; null hides Follow and its menu. */
    page: ArtistPageState | null;
    canPlay: boolean;
    onplay: () => void;
    onshuffle: () => void;
  };
  const { page, canPlay, onplay, onshuffle }: Props = $props();

  const follow = $derived(page?.follow ?? null);
  // Follow needs a catalog artist the page trusts; until then the button waits rather than guessing.
  const canFollow = $derived(Boolean(page && (follow || page.catalogArtistId)));

  function toggleKind(kind: ArtistReleaseKind, on: boolean) {
    const next = follow && toggleReleaseKind(follow.releaseKinds, kind, on);
    if (next) void page?.updateFollow({ releaseKinds: next });
  }
</script>

<!-- Spotify's artist row: the big round Play, Shuffle beside it, then Follow and More. Play is the
     page's one prominent action; Follow is an outlined pill that says what it is doing. -->
<div class="flex items-center gap-3 px-4 md:gap-4 md:px-0">
  {#if canPlay}
    <button
      type="button"
      class="bg-primary text-primary-foreground focus-visible:ring-ring grid size-14 shrink-0 place-items-center rounded-full shadow-[0_6px_18px_rgb(0_0_0/0.22)] transition-transform duration-150 focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:outline-hidden active:scale-95 md:hover:scale-105"
      aria-label="Play"
      title="Play"
      onclick={onplay}
    >
      <Play class="size-6 translate-x-px" fill="currentColor" />
    </button>
    <Button
      variant="ghost"
      size="icon-lg"
      class="size-11 rounded-full"
      aria-label="Shuffle"
      title="Shuffle"
      onclick={onshuffle}
    >
      <Shuffle class="size-6" />
    </Button>
  {/if}

  {#if page}
    <button
      type="button"
      class="border-border hover:border-foreground focus-visible:ring-ring text-subheadline relative inline-flex h-8 items-center gap-1.5 rounded-full border px-4 font-semibold transition-colors after:absolute after:inset-x-0 after:-inset-y-1.5 after:content-[''] focus-visible:ring-2 focus-visible:outline-hidden disabled:opacity-50 md:text-sm"
      aria-pressed={follow ? 'true' : 'false'}
      disabled={!canFollow || page.followBusy}
      title={canFollow ? undefined : 'Waiting for the catalog to confirm who this is'}
      onclick={() => void page.toggleFollow()}
    >
      {#if page.followBusy}<Spinner class="size-3.5" />{/if}
      {follow ? 'Following' : 'Follow'}
    </button>

    {#if follow}
      <DropdownMenu.Root>
        <DropdownMenu.Trigger>
          {#snippet child({ props })}
            <Button
              {...props}
              variant="ghost"
              size="icon-lg"
              class="size-11 rounded-full"
              aria-label="Follow settings"
            >
              <Ellipsis class="size-6" />
            </Button>
          {/snippet}
        </DropdownMenu.Trigger>
        <DropdownMenu.Content align="start" class="w-64">
          <DropdownMenu.Label>
            New releases
            <span class="text-muted-foreground block text-xs font-normal">
              {followSummary(follow.releaseKinds, follow.autoDownload)}
            </span>
          </DropdownMenu.Label>
          {#each ALL_KINDS as kind (kind)}
            <DropdownMenu.CheckboxItem
              checked={follow.releaseKinds.includes(kind)}
              closeOnSelect={false}
              onCheckedChange={(v) => toggleKind(kind, v)}
            >
              {KIND_PLURAL[kind]}
            </DropdownMenu.CheckboxItem>
          {/each}
          <DropdownMenu.Separator />
          <DropdownMenu.CheckboxItem
            checked={follow.autoDownload}
            closeOnSelect={false}
            onCheckedChange={(v) => void page.updateFollow({ autoDownload: v })}
          >
            <span class="flex flex-col">
              <span>Download automatically</span>
              <span class="text-muted-foreground text-xs">Off: only tell me about them</span>
            </span>
          </DropdownMenu.CheckboxItem>
          <DropdownMenu.Separator />
          <DropdownMenu.Item>
            {#snippet child({ props })}
              <a {...props} href="/following">All followed artists</a>
            {/snippet}
          </DropdownMenu.Item>
          <DropdownMenu.Item variant="destructive" onclick={() => void page.toggleFollow()}>
            Unfollow
          </DropdownMenu.Item>
        </DropdownMenu.Content>
      </DropdownMenu.Root>
    {/if}
  {/if}
</div>
