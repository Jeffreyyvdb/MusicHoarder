<script lang="ts" module>
  import type { LyricsProvenance } from '#lib/types.ts';

  const AI_LYRICS_COPY: Record<
    Exclude<LyricsProvenance, 'Human'>,
    { label: string; detail: string }
  > = {
    AiEnhanced: {
      label: 'Lyrics synced by AI',
      detail:
        "These are the song's own lyrics. Only the timing was adjusted by AI, to line the words up with this recording."
    },
    AiGenerated: {
      label: 'Lyrics transcribed by AI',
      detail:
        'An AI transcribed these lyrics from the audio. No published lyrics were available for this track, so the words may be wrong.'
    }
  };
</script>

<script lang="ts">
  import Sparkles from '@lucide/svelte/icons/sparkles';
  import { Badge } from '#lib/components/ui/badge/index.ts';
  import { cn } from '#lib/utils.ts';

  type Props = {
    provenance?: LyricsProvenance | null;
    /**
     * 'theater' is the full-screen / share player, where the badge floats over artwork and has to
     * stay legible without competing with the lyrics themselves. Tokens, not white-on-black: in
     * Now Playing they resolve to its media appearance (white/72 on black/70), on the share page
     * to the theme (muted grey on the page colour) — the old white/70 read at 1.8:1 in light.
     */
    variant?: 'panel' | 'theater';
    class?: string;
  };

  const { provenance, variant = 'panel', class: className }: Props = $props();

  const copy = $derived(provenance && provenance !== 'Human' ? AI_LYRICS_COPY[provenance] : null);
</script>

<!--
  The AI disclosure. It renders wherever lyrics are read — the docked panel, the full-screen player
  and the public share page — because the claim it makes is about the words on the screen, not about
  who happens to be looking at them.

  Two labels, deliberately not one: "synced by AI" means a machine moved timestamps under the real
  lyric, and "transcribed by AI" means a machine chose the words. Collapsing them would let the
  weaker disclosure cover the stronger case. Each label names the lyrics because in Now Playing the
  chip sits under the song's title, where a bare "AI generated" read as a claim about the song.
-->
{#if copy}
  <Badge
    variant="outline"
    title={copy.detail}
    aria-label={`${copy.label}. ${copy.detail}`}
    class={cn(
      'gap-1',
      variant === 'theater'
        ? 'border-border bg-background/70 text-muted-foreground'
        : 'text-muted-foreground',
      className
    )}
  >
    <Sparkles aria-hidden="true" />
    {copy.label}
  </Badge>
{/if}
