<script lang="ts">
  import type { Component, Snippet } from 'svelte';
  import { Button } from '$lib/components/ui/button';
  import { Skeleton } from '$lib/components/ui/skeleton';

  // The three non-content states every Inbox queue shares, so they cannot drift apart: a
  // skeleton shaped like the list that will replace it, a load error with Retry, and an empty
  // state that says why the queue is empty. They render inside the queue's scroller, under its nav
  // bar — a phone always keeps Back to the Inbox, whatever the queue is doing.
  type Props =
    | { state: 'loading'; label: string; rows?: number; compact: boolean }
    | { state: 'error'; message: string; onretry: () => void }
    | { state: 'empty'; icon: Component; title: string; children: Snippet };

  const props: Props = $props();
</script>

{#if props.state === 'loading'}
  <div role="status" class="md:p-1.5">
    <span class="sr-only">{props.label}</span>
    {#each Array(props.rows ?? 6) as _, i (i)}
      <div
        aria-hidden="true"
        class="flex min-h-[60px] items-center gap-3 pl-4 md:mb-0.5 md:min-h-0 md:gap-2.5 md:py-2 md:pr-2.5 md:pl-2.5"
      >
        <Skeleton class="shrink-0 rounded-sm {props.compact ? 'size-11' : 'size-10'}" />
        <div class="min-w-0 flex-1 space-y-2 pr-4 md:space-y-1.5 md:pr-0">
          <Skeleton class="h-4 w-3/4 md:h-3.5" />
          <Skeleton class="h-3.5 w-1/2 md:h-3" />
        </div>
      </div>
    {/each}
  </div>
{:else if props.state === 'error'}
  <div class="flex flex-col items-center gap-4 px-8 py-16 text-center">
    <p class="text-destructive-text text-body max-w-md md:text-sm">{props.message}</p>
    <Button class="h-11 rounded-full px-5 md:h-8 md:rounded-lg md:px-3" onclick={props.onretry}
      >Retry</Button
    >
  </div>
{:else}
  {@const Icon = props.icon}
  <div class="flex flex-col items-center gap-3 px-8 py-16 text-center">
    <span
      class="bg-primary/12 text-primary grid size-14 place-items-center rounded-full md:size-12"
    >
      <Icon class="size-7 md:size-6" aria-hidden="true" />
    </span>
    <h2 class="text-headline md:text-[15px]">{props.title}</h2>
    <p class="text-subheadline text-muted-foreground max-w-sm text-balance md:text-[12.5px]">
      {@render props.children()}
    </p>
  </div>
{/if}
