<script lang="ts">
  import { Card, CardContent, CardHeader, CardTitle } from '$lib/components/ui/card';
  import { AlertCircle } from '@lucide/svelte';
</script>

<div class="flex min-h-0 flex-1 flex-col gap-6 overflow-y-auto p-6">
  <div>
    <h1 class="text-2xl font-bold">Overview</h1>
    <p class="text-muted-foreground mt-1 text-sm">Pipeline status and live progress</p>
  </div>

  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2 text-base">
        <AlertCircle class="text-amber-500 size-5" />
        Page port pending
      </CardTitle>
    </CardHeader>
    <CardContent class="text-muted-foreground text-sm">
      <p>
        This page (dashboard tiles, trigger buttons for scan/fingerprint/enrich/build, SSE-driven
        live progress via <code>openProgressStream</code>) ports from
        <code>frontend/app/(app)/overview/page.tsx</code> (722 lines) and needs the full React → Svelte
        5 translation. The store + API client are already in place
        (<code>$lib/api-client.ts</code>, <code>$lib/stores/player.svelte</code>), so this is a
        UI-only port.
      </p>
    </CardContent>
  </Card>
</div>
