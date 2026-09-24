<script lang="ts">
  import { HardDrive, Link } from '@lucide/svelte';
  import { page } from '$app/state';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import AddFromUrlDialog from '$lib/components/v2/AddFromUrlDialog.svelte';
  import { NAV_GROUPS, type NavGroupId, type NavItem } from '$lib/nav';
  import { inboxCounts, refreshInboxNameCounts } from '$lib/stores/nav-badges.svelte';
  import { pipelineOverlay, type StageKey } from '$lib/stores/pipeline-overlay.svelte';
  import { storageUsage } from '$lib/stores/storage-usage.svelte';
  import { storageSummary, watchedFolders, watchingLabel } from '$lib/storage-usage-meta';
  import { isAdmin } from '$lib/auth/capabilities';

  // A group's hub: the root of its tab on a phone — a grouped list of the group's pages, the iOS
  // Settings / Music Library pattern — and, for Add and Manage, a page of its own on a desktop
  // too (/add, /manage), where the same list sits in a centred column. Listen's hub is the
  // Overview itself, so it has no variant here.
  //
  // The rows are built from $lib/nav, never a hand-kept list, so a page added to a group shows up
  // here the day it lands. What the rows add on top — counts, live status, storage — follows the
  // chrome's gating: Demo keeps the full nav but none of the admin-only live data (it has no
  // pipeline stream, no storage, no Add-from-link).
  type Props = { group: Exclude<NavGroupId, 'listen'> };
  const { group }: Props = $props();

  const navGroup = $derived(NAV_GROUPS.find((g) => g.id === group));
  const admin = $derived(isAdmin(page.data.user));
  const version = $derived(page.data.appVersion as string | null | undefined);

  function items(ids: readonly string[]): NavItem[] {
    return (navGroup?.items ?? []).filter((item) => ids.includes(item.id));
  }

  // Sections by item id; anything the nav gains that is not placed here lands in a final section
  // rather than disappearing.
  type Section = { header?: string; footer?: string; ids: string[] };
  function sectionsFor(spec: Section[]): { header?: string; footer?: string; rows: NavItem[] }[] {
    const placed = new Set(spec.flatMap((s) => s.ids));
    const sections = spec.map((s) => ({ header: s.header, footer: s.footer, rows: items(s.ids) }));
    const unplaced = (navGroup?.items ?? []).filter((item) => !placed.has(item.id));
    if (unplaced.length > 0)
      sections.push({ header: undefined, footer: undefined, rows: unplaced });
    return sections;
  }

  // ── Inbox ─────────────────────────────────────────────────────────────────
  // Every row carries its queue's size, counted exactly as that queue lists it — the same figures
  // the tab badge adds up (nav-badges.svelte.ts), so the badge is the sum of the first section and
  // no row promises items its page does not show. A blank value means "not known yet", never zero.
  // The name-merge suggestions sit apart: they are not in the badge, and their counts are fetched
  // only while this hub is on screen.
  const INBOX_SECTIONS: Section[] = [
    {
      ids: ['review', 'dupes', 'aiflag'],
      footer: 'Decisions the pipeline could not make on its own.'
    },
    {
      header: 'Suggestions',
      ids: ['dupe-artists', 'dupe-albums'],
      footer: 'Spellings of one artist or album that could be merged.'
    }
  ];
  const inboxSections = $derived(group === 'inbox' ? sectionsFor(INBOX_SECTIONS) : []);
  const counts = $derived(inboxCounts());
  function inboxCount(id: string): number | null {
    switch (id) {
      case 'review':
        return counts.review;
      case 'dupes':
        return counts.dupes;
      case 'aiflag':
        return counts.ai;
      case 'dupe-artists':
        return counts.artists;
      case 'dupe-albums':
        return counts.albums;
      default:
        return null;
    }
  }
  // Admin only: the name-merge endpoints are admin-gated (a demo's queues there read an error too).
  $effect(() => {
    if (group === 'inbox' && admin) void refreshInboxNameCounts();
  });

  // ── Add ───────────────────────────────────────────────────────────────────
  let addOpen = $state(false);

  // ── Manage ────────────────────────────────────────────────────────────────
  const MANAGE_SECTIONS: Section[] = [
    { ids: ['pipeline'] },
    { header: 'Library', ids: ['folders', 'quality', 'album-quality'] },
    { header: 'Insights', ids: ['performance', 'stats', 'history'] },
    { ids: ['settings'] }
  ];
  const manageSections = $derived.by(() => {
    const sections = sectionsFor(MANAGE_SECTIONS);
    // Unplaced items go before the Settings section, which closes the list with Storage and the
    // footer.
    if (sections.length > MANAGE_SECTIONS.length) {
      const unplaced = sections.pop()!;
      sections.splice(sections.length - 1, 0, unplaced);
    }
    return sections;
  });

  // The Pipeline row's live status. The stream runs only while something asks for it; this hub
  // asks while it is on screen (like the Pipeline page), and only for an admin.
  $effect(() => {
    if (group === 'manage' && admin) return pipelineOverlay.keepLive();
  });
  const STAGES: StageKey[] = ['scan', 'fingerprint', 'enrich', 'build'];
  const snapshot = $derived(pipelineOverlay.snapshot);
  const pipelineState = $derived.by<'running' | 'paused' | 'idle' | null>(() => {
    if (!admin || !snapshot) return null;
    if (pipelineOverlay.isAnyRunning) return 'running';
    if (STAGES.some((s) => pipelineOverlay.isStagePaused(s))) return 'paused';
    return 'idle';
  });
  const PIPELINE_LABEL = { running: 'Running', paused: 'Paused', idle: 'Idle' } as const;
  // Each file passes four stages, so the job is done at discovered × 4 stage-steps.
  const pipelineProgress = $derived.by(() => {
    if (pipelineState !== 'running' || !snapshot || snapshot.discovered <= 0) return null;
    return Math.max(0, Math.min(1, pipelineOverlay.processed / (snapshot.discovered * 4)));
  });

  // The same figure and bar as the sidebar footer and the account panel (storageSummary).
  const storage = $derived(storageSummary(storageUsage.snapshot, storageUsage.computing));
  const storageValue = $derived(storage?.label);
  const storageSegments = $derived(storage?.segments ?? []);

  const footer = $derived(
    [
      `MusicHoarder${version ? ` v${version}` : ''}`,
      admin ? watchingLabel(watchedFolders(pipelineOverlay.overview).length) : null
    ]
      .filter(Boolean)
      .join(' · ')
  );
</script>

{#snippet navRow(item: NavItem, value?: number | string | null)}
  <GroupedList.Row
    href={item.href}
    icon={item.icon}
    label={item.label}
    value={value ?? undefined}
    chevron
  />
{/snippet}

{#if navGroup}
  <!-- The page's one scroller. The nav bar is its first child so the large title scrolls away
       with the list; the bottom padding clears the tab bar and the MiniPlayer (--mh-content-pad). -->
  <div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
    <div class="min-h-0 flex-1 overflow-y-auto overscroll-contain pb-(--mh-content-pad)">
      <PageToolbarV2 title={navGroup.label} grouped largeTitle />

      <div class="mx-auto flex w-full max-w-2xl flex-col gap-8 pt-2 pb-8 md:px-6 md:pt-8">
        {#if group === 'inbox'}
          {#each inboxSections as section, i (i)}
            {#if section.rows.length > 0}
              <GroupedList.Section headingLevel={2} header={section.header} footer={section.footer}>
                {#each section.rows as item (item.id)}
                  {@render navRow(item, inboxCount(item.id))}
                {/each}
              </GroupedList.Section>
            {/if}
          {/each}
        {:else if group === 'add'}
          {#if admin}
            <GroupedList.Section
              footer="Paste a Spotify track or YouTube link to download it into your library."
            >
              <GroupedList.Row icon={Link} onclick={() => (addOpen = true)}>
                <span class="text-body text-primary md:text-sm">Add from link…</span>
              </GroupedList.Row>
            </GroupedList.Section>
          {/if}
          <GroupedList.Section>
            {#each navGroup.items as item (item.id)}
              {@render navRow(item)}
            {/each}
          </GroupedList.Section>
        {:else}
          {#each manageSections as section, i (i)}
            {@const last = i === manageSections.length - 1}
            {#if section.rows.length > 0 || last}
              <GroupedList.Section
                headingLevel={2}
                header={section.header}
                footer={last ? footer : undefined}
              >
                {#if last && admin}
                  <!-- Storage opens the breakdown sheet over the hub (the shell mounts it). -->
                  <GroupedList.Row
                    icon={HardDrive}
                    label="Storage"
                    value={storageValue}
                    chevron
                    onclick={() => (storageUsage.dialogOpen = true)}
                  >
                    {#if storageSegments.length > 0}
                      <span
                        class="bg-muted mt-1.5 mb-0.5 flex h-1 w-full overflow-hidden rounded-full"
                        aria-hidden="true"
                      >
                        {#each storageSegments as segment (segment.key)}
                          <span class="{segment.color} h-full" style="width: {segment.pct}%"></span>
                        {/each}
                      </span>
                    {/if}
                  </GroupedList.Row>
                {/if}
                {#each section.rows as item (item.id)}
                  {#if item.id === 'pipeline' && pipelineState}
                    <GroupedList.Row
                      href={item.href}
                      icon={item.icon}
                      iconClass={pipelineState === 'running'
                        ? 'bg-primary text-primary-foreground'
                        : undefined}
                      label={item.label}
                      value={PIPELINE_LABEL[pipelineState]}
                      chevron
                    >
                      {#if pipelineProgress != null}
                        <span
                          role="progressbar"
                          aria-label="Pipeline progress"
                          aria-valuemin={0}
                          aria-valuemax={100}
                          aria-valuenow={Math.round(pipelineProgress * 100)}
                          class="bg-muted mt-1.5 mb-0.5 block h-1 w-full overflow-hidden rounded-full"
                        >
                          <span
                            class="bg-primary block h-full w-full origin-left transition-transform duration-300"
                            style="transform: scaleX({pipelineProgress})"
                          ></span>
                        </span>
                      {/if}
                    </GroupedList.Row>
                  {:else}
                    {@render navRow(item)}
                  {/if}
                {/each}
              </GroupedList.Section>
            {/if}
          {/each}
        {/if}
      </div>
    </div>
  </div>

  {#if group === 'add' && admin}
    <AddFromUrlDialog bind:open={addOpen} />
  {/if}
{/if}
