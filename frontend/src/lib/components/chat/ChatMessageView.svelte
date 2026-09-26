<script lang="ts">
  import type { ChatMessage } from '$lib/api-client';
  import { linkSegments, timeLabel } from '$lib/chat/chat-text';
  import { cn } from '$lib/utils';
  import ChatLinkCard from './ChatLinkCard.svelte';
  import ChatShareCard from './ChatShareCard.svelte';

  // One message. Yours sit on the right in the tint, theirs on the left on the gray fill — the
  // messaging convention both platforms share. A song or link rides under the words as a card. A
  // share-link open is not something anyone wrote: it is centred under the server's line saying why
  // it is in your chats, the way Spotify and TikTok file a link you opened under whoever sent it.
  type Props = {
    message: ChatMessage;
    /** Same sender as the message above, moments later: no gap. */
    continues?: boolean;
    /** The last of such a run, which carries the time. */
    lastOfRun?: boolean;
    onadd?: (url: string) => void;
  };
  const { message, continues = false, lastOfRun = true, onadd }: Props = $props();

  const text = $derived(message.text?.trim() ? message.text : null);
  const segments = $derived(text ? linkSegments(text) : []);
</script>

{#if message.kind === 'shareOpened'}
  <div class="flex flex-col items-center gap-2 px-4 py-3">
    {#if message.notice}
      <p class="text-footnote text-muted-foreground max-w-sm text-center text-balance md:text-xs">
        {message.notice}
      </p>
    {/if}
    {#if message.share}
      <ChatShareCard share={message.share} />
    {/if}
  </div>
{:else}
  <div
    class={cn(
      'flex flex-col px-4',
      message.mine ? 'items-end' : 'items-start',
      continues ? 'mt-0.5' : 'mt-3'
    )}
  >
    {#if text}
      <p
        class={cn(
          'text-body max-w-[min(80%,34rem)] rounded-[20px] px-3.5 py-2 break-words whitespace-pre-wrap md:text-sm',
          message.mine ? 'bg-primary text-primary-foreground' : 'bg-secondary text-foreground'
        )}
      >
        {#each segments as segment, i (i)}
          {#if segment.kind === 'link'}
            <a href={segment.href} target="_blank" rel="noopener noreferrer" class="underline underline-offset-2"
              >{segment.value}</a
            >
          {:else}{segment.value}{/if}
        {/each}
      </p>
    {/if}
    {#if message.share}
      <ChatShareCard share={message.share} class={cn(text && 'mt-1')} />
    {/if}
    {#if message.link}
      <ChatLinkCard link={message.link} {onadd} class={cn(text && 'mt-1')} />
    {/if}
    {#if lastOfRun}
      <time
        datetime={message.createdAtUtc}
        class="text-caption-2 text-muted-foreground mt-1 px-1 tabular-nums md:text-[11px]"
      >
        {timeLabel(message.createdAtUtc)}
      </time>
    {/if}
  </div>
{/if}
