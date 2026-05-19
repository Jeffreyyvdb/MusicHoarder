<script lang="ts">
  import { Database, Cpu, Globe } from '@lucide/svelte';

  const techItems = [
    { icon: Cpu, label: '.NET 10 API' },
    { icon: Globe, label: 'SvelteKit Frontend' },
    { icon: Database, label: 'PostgreSQL' }
  ];
</script>

<section class="bg-card/30 border-border border-t px-6 py-16">
  <div class="mx-auto max-w-4xl">
    <div class="flex flex-col items-center justify-between gap-8 md:flex-row">
      <div>
        <h3 class="mb-2 text-xl font-semibold">Built for homelabs</h3>
        <p class="text-muted-foreground text-sm">
          Modern stack designed for self-hosting enthusiasts.
        </p>
      </div>

      <div class="flex flex-wrap items-center justify-center gap-6">
        {#each techItems as item (item.label)}
          <div
            class="bg-card border-border flex items-center gap-2 rounded-lg border px-4 py-2"
          >
            <item.icon class="text-primary h-4 w-4" />
            <span class="text-sm font-medium">{item.label}</span>
          </div>
        {/each}
      </div>
    </div>
  </div>
</section>
