<script lang="ts">
  // Now Playing's Video mode under the hardware skin: the MH-2, a monitor beside the MH-1 player. The
  // clip is its screen, set in the same bezel as the MH-1's cover; under it the sync line sits on a
  // gray LCD next to a fullscreen key, and the plate's LED is lit while the clip plays. A housing
  // for VideoWatchTab, which still owns the picture (its sync, its taps, fullscreen) and fits it so
  // the whole device fits. TrackPanel loads this lazily, with the rest of the skin's devices.
  //
  // Hand-built rather than a Device: Device zooms itself down to fit a narrow page, and the picture
  // is already fitted — a zoomed ancestor would also scale the fullscreen overlay inside it.
  import type { VideoHousing } from '#lib/components/file-browser/VideoWatchTab.svelte';
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
