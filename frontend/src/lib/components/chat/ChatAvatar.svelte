<script lang="ts">
  import { albumTint } from '$lib/album-tint';
  import type { ChatPerson } from '$lib/api-client';
  import { initials } from '$lib/chat/chat-text';
  import { cn } from '$lib/utils';

  // A person as a tinted circle with their initials. The tint is the album-cover hash on the
  // account id, so one person keeps one colour everywhere and nobody's is chosen by hand.
  type Props = { person: Pick<ChatPerson, 'id' | 'name'> | null | undefined; size?: number; class?: string };
  const { person, size = 40, class: className }: Props = $props();

  const tint = $derived(albumTint(person?.id ?? '', person?.name ?? ''));
</script>

<span
  aria-hidden="true"
  class={cn(
    'grid shrink-0 place-items-center rounded-full font-semibold text-white select-none',
    className
  )}
  style:width="{size}px"
  style:height="{size}px"
  style:font-size="{Math.max(11, Math.round(size * 0.38))}px"
  style:background-image="linear-gradient(135deg, {tint.from}, {tint.to})"
>
  {initials(person)}
</span>
