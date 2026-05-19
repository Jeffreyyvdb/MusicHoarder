<script lang="ts">
  import { Card, CardContent, CardHeader, CardTitle } from '$lib/components/ui/card';
  import { AlertCircle } from '@lucide/svelte';
</script>

<div class="flex min-h-0 flex-1 flex-col gap-6 overflow-y-auto p-6">
  <div>
    <h1 class="text-2xl font-bold">Review</h1>
    <p class="text-muted-foreground mt-1 text-sm">Manual metadata review queue</p>
  </div>

  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2 text-base">
        <AlertCircle class="text-amber-500 size-5" />
        Review queue port pending
      </CardTitle>
    </CardHeader>
    <CardContent class="text-muted-foreground text-sm">
      <p>
        Ports from <code>frontend/app/(app)/review/page.tsx</code> (972 lines). Side-by-side metadata
        comparison (original vs candidate), batch accept/reject, provider attempt inspector, and the
        sticky-header table pattern. Calls
        <code>patchSongMetadata</code>/<code>batchPatchSongs</code>/<code>resetEnrichmentForSong</code>
        (already in <code>$lib/api-client</code>).
      </p>
    </CardContent>
  </Card>
</div>
