<script lang="ts">
  import type { LyricsStatus } from '$lib/types';
  import { Badge } from '$lib/components/ui/badge';
  import { CircleCheck, Music, FileText, AlertCircle, Clock } from '@lucide/svelte';
  type Props = { status?: LyricsStatus };
  const { status }: Props = $props();
</script>

<!-- Every state carries a glyph and a word, so colour is never the only signal; the tints are the
     contrast-checked Badge variants rather than hard-coded hues. A good state is a grey label with
     the success glyph in the tint, not green text: green text is kept for things you can tap. -->
{#if status === 'Fetched'}
  <Badge variant="secondary">
    <CircleCheck class="text-primary" />
    Lyrics found
  </Badge>
{:else if status === 'Instrumental'}
  <Badge variant="secondary">
    <Music />
    Instrumental
  </Badge>
{:else if status === 'NotFound'}
  <Badge variant="secondary">
    <FileText />
    No lyrics found
  </Badge>
{:else if status === 'Failed'}
  <Badge variant="destructive">
    <AlertCircle />
    Fetch failed
  </Badge>
{:else}
  <Badge variant="outline" class="text-muted-foreground">
    <Clock />
    Not fetched yet
  </Badge>
{/if}
