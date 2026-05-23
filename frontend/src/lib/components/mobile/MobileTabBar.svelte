<script lang="ts">
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { onMount } from 'svelte';
  import { Library, Play, TriangleAlert, Clock, Settings } from '@lucide/svelte';
  import { fetchOverview } from '$lib/api-client';

  type Tab = {
    id: string;
    label: string;
    href: string;
    icon: typeof Library;
    /** Matches the current route by prefix. */
    match: (path: string) => boolean;
  };

  const tabs: Tab[] = [
    { id: 'library', label: 'Library', href: '/library', icon: Library, match: (p) => p.startsWith('/library') },
    { id: 'spotify', label: 'Spotify', href: '/spotify', icon: Play, match: (p) => p.startsWith('/spotify') },
    { id: 'review', label: 'Review', href: '/review', icon: TriangleAlert, match: (p) => p.startsWith('/review') },
    { id: 'runs', label: 'Runs', href: '/runs', icon: Clock, match: (p) => p.startsWith('/runs') },
    { id: 'profile', label: 'Profile', href: '/settings', icon: Settings, match: (p) => p.startsWith('/settings') }
  ];

  let reviewCount = $state(0);

  async function refreshBadge() {
    try {
      const overview = await fetchOverview();
      reviewCount = overview.enrichment?.needsReview ?? overview.job.tracksReview ?? 0;
    } catch {
      // leave the last good count
    }
  }

  onMount(() => {
    void refreshBadge();
    const handle = setInterval(refreshBadge, 30000);
    return () => clearInterval(handle);
  });

  const activeId = $derived(tabs.find((t) => t.match(page.url.pathname))?.id ?? '');
</script>

<nav class="mob-tabs">
  {#each tabs as tab (tab.id)}
    {@const Icon = tab.icon}
    <button
      class="mob-tab {activeId === tab.id ? 'active' : ''}"
      onclick={() => goto(tab.href)}
      aria-current={activeId === tab.id ? 'page' : undefined}
    >
      <Icon size={22} strokeWidth={1.5} />
      <span>{tab.label}</span>
      {#if tab.id === 'review' && reviewCount > 0}
        <span class="mob-tab-badge">{reviewCount > 99 ? '99+' : reviewCount}</span>
      {/if}
    </button>
  {/each}
</nav>
