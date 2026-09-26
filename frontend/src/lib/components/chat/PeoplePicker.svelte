<script lang="ts">
  import { Check, Users } from '@lucide/svelte';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { EmptyState } from '$lib/components/ui/empty-state';
  import { SearchField } from '$lib/components/ui/search-field';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import type { ChatPerson } from '$lib/api-client';
  import { chatStore } from '$lib/stores/chat.svelte';
  import { cn } from '$lib/utils';
  import ChatAvatar from './ChatAvatar.svelte';

  // Who to send something to: the people you can reach, those you talk to most recently first,
  // each a row with a check (several at once) or a plain tap (one, for "New message").
  type Props = {
    people: ChatPerson[];
    loading?: boolean;
    /** Ids picked so far (multiple mode). */
    selected?: string[];
    multiple?: boolean;
    onpick: (person: ChatPerson) => void;
  };
  const { people, loading = false, selected = [], multiple = false, onpick }: Props = $props();

  let query = $state('');

  const ordered = $derived.by(() => {
    const recency = new Map<string, number>();
    chatStore.conversations.forEach((c, index) => {
      for (const m of c.members) if (!recency.has(m.id)) recency.set(m.id, index);
    });
    const q = query.trim().toLowerCase();
    return people
      .filter((p) => !q || p.name.toLowerCase().includes(q) || (p.email ?? '').toLowerCase().includes(q))
      .sort(
        (a, b) =>
          (recency.get(a.id) ?? Number.MAX_SAFE_INTEGER) - (recency.get(b.id) ?? Number.MAX_SAFE_INTEGER) ||
          a.name.localeCompare(b.name)
      );
  });
</script>

<div class="flex flex-col gap-4">
  {#if people.length > 6}
    <SearchField bind:value={query} label="Search people" class="mx-4 md:mx-0" />
  {/if}

  {#if loading}
    <GroupedList.Section>
      {#each Array(3) as _, i (i)}
        <div class="flex items-center gap-3 px-4 py-2.5">
          <Skeleton class="size-10 shrink-0 rounded-full" />
          <Skeleton class="h-4 w-1/2" />
        </div>
      {/each}
    </GroupedList.Section>
  {:else if people.length === 0}
    <EmptyState
      icon={Users}
      title="Nobody to send to yet"
      hint="You can chat with the administrators of this MusicHoarder and the people you share music with."
    />
  {:else if ordered.length === 0}
    <EmptyState
      icon={Users}
      title="No one by that name"
      action={{ label: 'Clear search', onclick: () => (query = '') }}
    />
  {:else}
    <GroupedList.Section>
      {#each ordered as person (person.id)}
        {@const picked = selected.includes(person.id)}
        <GroupedList.Row
          label={person.name}
          sublabel={person.email ?? (person.isAdmin ? 'Administrator' : undefined)}
          onclick={() => onpick(person)}
          aria-pressed={multiple ? picked : undefined}
        >
          {#snippet leading()}
            <ChatAvatar {person} size={40} />
          {/snippet}
          {#snippet trailing()}
            {#if multiple}
              <span
                aria-hidden="true"
                class={cn(
                  'grid size-6 place-items-center rounded-full border-2',
                  picked ? 'bg-primary border-primary text-primary-foreground' : 'border-muted-foreground-dim'
                )}
              >
                {#if picked}<Check class="size-4" strokeWidth={3} />{/if}
              </span>
            {/if}
          {/snippet}
        </GroupedList.Row>
      {/each}
    </GroupedList.Section>
  {/if}
</div>
