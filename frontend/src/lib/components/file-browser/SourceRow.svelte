<script lang="ts">
  import { CircleCheck, ExternalLink } from '@lucide/svelte';
  import * as GroupedList from '$lib/components/ui/grouped-list';

  type Props = {
    name: string;
    connected?: boolean;
    url?: string;
    label?: string;
  };
  const { name, connected, url, label }: Props = $props();
</script>

<!--
  One enrichment source as a grouped-list cell: a check (matched) or an empty ring (not), the
  source's name with the link's short form under it (a URL is too long to share a phone row with
  the name), and — when there is one — the external link as the row itself. The state is carried
  by the glyph's shape and a spoken word, never by colour alone.
-->
<GroupedList.Row
  href={url}
  target={url ? '_blank' : undefined}
  rel={url ? 'noopener noreferrer' : undefined}
  label={name}
  sublabel={label}
  title={url}
>
  {#snippet leading()}
    {#if connected}
      <CircleCheck class="text-primary size-5 shrink-0" aria-hidden="true" />
    {:else}
      <span class="border-muted-foreground size-5 shrink-0 rounded-full border-2" aria-hidden="true"></span>
    {/if}
    <span class="sr-only">{connected ? 'Matched:' : 'Not matched:'}</span>
  {/snippet}
  {#snippet trailing()}
    {#if url}
      <ExternalLink class="text-muted-foreground size-4 shrink-0" aria-hidden="true" />
    {/if}
  {/snippet}
</GroupedList.Row>
