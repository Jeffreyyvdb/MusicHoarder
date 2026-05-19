<script lang="ts">
  import { Card, CardContent, CardHeader, CardTitle } from '$lib/components/ui/card';
  import { AlertCircle } from '@lucide/svelte';
</script>

<div class="flex min-h-0 flex-1 flex-col gap-6 overflow-y-auto p-6">
  <div>
    <h1 class="text-2xl font-bold">Library</h1>
    <p class="text-muted-foreground mt-1 text-sm">Browse your music files</p>
  </div>

  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2 text-base">
        <AlertCircle class="text-amber-500 size-5" />
        File browser port pending
      </CardTitle>
    </CardHeader>
    <CardContent class="text-muted-foreground text-sm">
      <p>
        The file browser ports from <code>frontend/components/file-browser/</code> (8 files, ~3000
        lines total): file tree, breadcrumb, toolbar, file grid, album grid + detail views, track
        details panel. Backed by <code>fetchSongs()</code> and <code>buildFileSystemFromSongs()</code>
        in <code>$lib/api-client</code> (already ported). Uses resizable panels and requires
        <code>min-h-0</code> on every flex ancestor of the scroll viewport.
      </p>
    </CardContent>
  </Card>
</div>
