<script lang="ts">
  import { page } from '$app/state';
  import { Card, CardContent, CardHeader, CardTitle } from '$lib/components/ui/card';
  import { AlertCircle } from '@lucide/svelte';

  const artistId = $derived(page.params.id);
</script>

<div class="flex min-h-0 flex-1 flex-col gap-6 overflow-y-auto p-6">
  <div>
    <h1 class="text-2xl font-bold">Artist {artistId}</h1>
    <p class="text-muted-foreground mt-1 text-sm">Discography view</p>
  </div>

  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2 text-base">
        <AlertCircle class="text-amber-500 size-5" />
        Artist detail port pending
      </CardTitle>
    </CardHeader>
    <CardContent class="text-muted-foreground text-sm">
      <p>
        Ports from <code>frontend/app/(app)/artists/[id]/page.tsx</code>. Renders a single artist's
        full discography with album/track in-library status and download actions.
      </p>
    </CardContent>
  </Card>
</div>
