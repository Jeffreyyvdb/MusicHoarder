<script lang="ts">
  import type { VideoHousing } from '#lib/components/file-browser/VideoWatchTab.svelte';
  import { MessageSquareQuote } from '@lucide/svelte';
  import Hardware from './Hardware.svelte';
  import ButtonGroup from './ButtonGroup.svelte';
  import Lcd from './Lcd.svelte';
  import './skin/devices.css';

  let {
    housing,
    status = null
  }: {
    housing: VideoHousing;
    /** The line under the picture: how the clip is aligned, or what is wrong with it. */
    status?: { text: string; tone: 'muted' | 'destructive' } | null;
  } = $props();
</script>

<Hardware bare>
  <section data-device aria-label="Video" data-mh-video>
    <header data-device-plate>
      <span data-device-power data-on={housing.playing || undefined} aria-hidden="true"></span>
      <strong>MH-2</strong>
      <span>Video</span>
    </header>

    <figure data-kit="display">
      <div data-kit="display-bezel">{@render housing.screen()}</div>
    </figure>

    <div data-mh-video-controls>
      {#if status}
        <Lcd
          tag={status.tone === 'destructive' ? 'Fault' : 'Sync'}
          size="sm"
          data-mh-video-status
          data-tone={status.tone}>{status.text}</Lcd
        >
      {/if}
      <div data-mh-video-key>
        <button
          type="button"
          class="flex size-11 items-center justify-center rounded-lg border border-black/15 bg-white/80 text-black shadow-sm focus-visible:outline-2 focus-visible:outline-offset-2 aria-pressed:bg-[#d2d2c8] aria-pressed:shadow-inner"
          aria-label="Lyrics"
          aria-pressed={housing.lyricsVisible}
          onclick={housing.toggleLyrics}><MessageSquareQuote class="size-5" /></button
        >
        <small aria-hidden="true">Lyrics</small>
      </div>
      <div data-mh-video-key>
        <ButtonGroup
          label="Fullscreen"
          parts={[
            {
              label: housing.expanded ? 'Exit fullscreen' : 'Watch fullscreen',
              icon: 'maximize',
              onPress: housing.toggleFullscreen
            }
          ]}
        />
        <small aria-hidden="true">Full</small>
      </div>
    </div>
  </section>
</Hardware>
