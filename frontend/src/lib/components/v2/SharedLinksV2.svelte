<script lang="ts">
  import { Copy, Link2Off, Loader2 } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import { Button } from '$lib/components/ui/button';
  import {
    listSongShares,
    revokeSongShare,
    shareUrl,
    type SongShareView
  } from '$lib/api-client';

  // Share links can be minted from an album page and from a track panel, but until now there was
  // nowhere to see or undo that: `GET /api/shares` and `DELETE /api/shares/{id}` both existed and
  // neither had a caller. Since links carry no expiry, revoking is the only way to turn one off.
  let shares = $state<SongShareView[] | null>(null);
  let revoking = $state<number | null>(null);
  let failed = $state(false);

  async function load(): Promise<void> {
    try {
      shares = await listSongShares();
      failed = false;
    } catch {
      failed = true;
    }
  }

  $effect(() => {
    void load();
  });

  async function copy(share: SongShareView): Promise<void> {
    try {
      await navigator.clipboard.writeText(shareUrl(share.token));
      toast.success('Link copied.');
    } catch {
      toast.error('Could not copy the link.');
    }
  }

  async function revoke(share: SongShareView): Promise<void> {
    revoking = share.id;
    try {
      await revokeSongShare(share.id);
      shares = (shares ?? []).filter((s) => s.id !== share.id);
      toast.success('Link revoked — it stops working immediately.');
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Could not revoke the link.');
    } finally {
      revoking = null;
    }
  }

  function fmtWhen(iso: string): string {
    return new Date(iso).toLocaleDateString([], {
      year: 'numeric',
      month: 'short',
      day: 'numeric'
    });
  }
</script>

<section class="border-border bg-card rounded-lg border">
  <header class="border-border border-b px-5 py-3.5">
    <h2 class="text-sm font-semibold">Shared links</h2>
    <p class="text-muted-foreground text-xs">
      Public links you've created for a track or album. Anyone with the link can stream it without
      signing in, and <strong class="font-medium">links never expire</strong> — revoking is the only
      way to switch one off.
    </p>
  </header>

  <div class="divide-border divide-y">
    {#if shares === null && !failed}
      <div class="text-muted-foreground px-5 py-4 text-xs">Loading…</div>
    {:else if failed}
      <div class="text-muted-foreground px-5 py-4 text-xs">
        Couldn't load your shared links.
      </div>
    {:else if shares && shares.length === 0}
      <div class="text-muted-foreground px-5 py-4 text-xs">
        No active links. You can create one from any album or track.
      </div>
    {:else if shares}
      {#each shares as share (share.id)}
        <div class="flex items-center gap-3 px-5 py-3">
          <div class="min-w-0 flex-1">
            <div class="truncate text-[12.5px] font-medium">{share.title}</div>
            <div class="text-muted-foreground truncate text-[11.5px]">
              {share.scope === 'Album' ? 'Whole album' : 'Single track'}
              {#if share.artist}· {share.artist}{/if}
              · shared {fmtWhen(share.createdAtUtc)}
            </div>
          </div>
          <Button
            variant="ghost"
            size="sm"
            class="text-muted-foreground hover:text-foreground shrink-0 gap-1.5"
            onclick={() => copy(share)}
          >
            <Copy class="size-3.5" />
            Copy
          </Button>
          <Button
            variant="ghost"
            size="sm"
            class="text-muted-foreground hover:text-destructive shrink-0 gap-1.5"
            disabled={revoking !== null}
            onclick={() => revoke(share)}
          >
            {#if revoking === share.id}
              <Loader2 class="size-3.5 animate-spin" />
            {:else}
              <Link2Off class="size-3.5" />
            {/if}
            Revoke
          </Button>
        </div>
      {/each}
    {/if}
  </div>
</section>
