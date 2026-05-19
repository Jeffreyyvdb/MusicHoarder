<script lang="ts">
  import type { LyricsStatus } from '$lib/types';
  import { Badge } from '$lib/components/ui/badge';
  import { CheckCircle2, Music, FileText, AlertCircle, Clock } from '@lucide/svelte';
  type Props = { status?: LyricsStatus };
  const { status }: Props = $props();
</script>

{#if status === 'Fetched'}
  <Badge class="bg-primary/10 text-primary hover:bg-primary/20 text-xs">
    <CheckCircle2 class="mr-1 size-3" />
    Lyrics Found
  </Badge>
{:else if status === 'Instrumental'}
  <Badge class="bg-blue-500/10 text-xs text-blue-600 hover:bg-blue-500/20 dark:text-blue-400">
    <Music class="mr-1 size-3" />
    Instrumental
  </Badge>
{:else if status === 'NotFound'}
  <Badge variant="secondary" class="text-xs">
    <FileText class="mr-1 size-3" />
    No Lyrics Found
  </Badge>
{:else if status === 'Failed'}
  <Badge variant="destructive" class="text-xs">
    <AlertCircle class="mr-1 size-3" />
    Fetch Failed
  </Badge>
{:else}
  <Badge variant="outline" class="text-muted-foreground text-xs">
    <Clock class="mr-1 size-3" />
    Not Fetched
  </Badge>
{/if}
