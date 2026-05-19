<script lang="ts">
  import { Card, CardContent, CardHeader, CardTitle } from '$lib/components/ui/card';
  import { AlertCircle } from '@lucide/svelte';
</script>

<div class="flex min-h-0 flex-1 flex-col gap-6 overflow-y-auto p-6">
  <div>
    <h1 class="text-2xl font-bold">Settings</h1>
    <p class="text-muted-foreground mt-1 text-sm">Configuration, Spotify credentials, library purge</p>
  </div>

  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2 text-base">
        <AlertCircle class="text-amber-500 size-5" />
        Settings port pending
      </CardTitle>
    </CardHeader>
    <CardContent class="text-muted-foreground text-sm">
      <p>
        Ports from <code>frontend/app/(app)/settings/page.tsx</code> (484 lines). Sections: provider
        toggles, Spotify client id/secret form, library purge controls with 1.5s polling of
        <code>purgeStatus</code>. Polling is a <code>setInterval</code> inside <code>$effect</code>
        with a cleanup return.
      </p>
    </CardContent>
  </Card>
</div>
