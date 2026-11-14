<script lang="ts" module>
  export interface HardwareTrack {
    /** Unique within the queue (a song can be queued twice). */
    key: string;
    title: string;
    artist: string;
    album?: string | null;
    coverUrl?: string | null;
    format?: string | null;
    /** Seconds, when the library knows it. */
    length?: number | null;
  }
</script>

<script lang="ts">
  // MusicHoarder's player as a SPEC-01 pocket device (after the kit's MP-1 "music-player" block): the
  // cover on a square screen, the LCD and transport rocker beside it, three latching cells (like,
  // mute, volume), the scrub fader under both, and the queue as an item list whose playing row fills
  // as it plays. Presentational: HardwareNowPlaying feeds it the player store.
  import Device from './Device.svelte';
  import Display from './Display.svelte';
  import Lcd from './Lcd.svelte';
  import LcdDigits from './LcdDigits.svelte';
  import ButtonGroup from './ButtonGroup.svelte';
  import Latch from './Latch.svelte';
  import FlatKnob from './FlatKnob.svelte';
  import Slider from './Slider.svelte';
  import ItemList, { type ItemListColumn } from './ItemList.svelte';
  import { clock } from './clock.ts';

  let {
    queue,
    currentKey,
    isPlaying,
    currentTime,
    duration,
    volume,
    liked,
    canLike,
    onPrevious,
    onToggle,
    onNext,
    onSeek,
    onVolume,
    onMute,
    onLike,
    onPick
  }: {
    /** The loaded track first, then what plays after it. */
    queue: readonly HardwareTrack[];
    currentKey: string | null;
    isPlaying: boolean;
    currentTime: number;
    duration: number;
    /** 0–1. */
    volume: number;
    liked: boolean;
    canLike: boolean;
    onPrevious: () => void;
    onToggle: () => void;
    onNext: () => void;
    onSeek: (seconds: number) => void;
    onVolume: (volume: number) => void;
    onMute: () => void;
    onLike: (liked: boolean) => void;
    onPick: (index: number) => void;
  } = $props();

  const index = $derived(queue.findIndex((t) => t.key === currentKey));
  const current = $derived(index >= 0 ? queue[index] : null);
  const length = $derived(
    Number.isFinite(duration) && duration > 0 ? duration : (current?.length ?? 0)
  );

  // The fader follows the music until a drag or a key takes it; the seek lands once, on commit, so a
  // drag doesn't fire a range request per step.
  let scrub = $state<number | null>(null);
  const position = $derived(scrub ?? currentTime);

  let coverFailed = $state(false);
  $effect(() => {
    void current?.coverUrl;
    coverFailed = false;
  });

  const tag = $derived(
    current
      ? `${String(index + 1).padStart(2, '0')} · ${(current.format ?? 'audio').toUpperCase()}`
      : 'No disc'
  );

  const columns: ItemListColumn<HardwareTrack>[] = [
    { key: 'n', label: 'No', width: '22px', cell: number },
    { key: 'title', label: 'Title', width: 'minmax(0, 1fr)', cell: title },
    { key: 'len', label: 'Time', width: '38px', align: 'end', cell: time }
  ];
</script>

{#snippet number(_row: HardwareTrack, i: number)}{String(i + 1).padStart(2, '0')}{/snippet}
{#snippet title(row: HardwareTrack)}{row.title}<span data-mh-artist>{row.artist}</span>{/snippet}
{#snippet time(row: HardwareTrack)}{row.length ? clock(row.length) : '–:––'}{/snippet}

<Device model="MH-1" name="Player" on={isPlaying}>
  <div data-player>
    <div data-player-screen>
      <Display aspect="1 / 1">
        <div data-art>
          {#if current?.coverUrl && !coverFailed}
            <img src={current.coverUrl} alt="" data-hw-cover onerror={() => (coverFailed = true)} />
          {:else}
            <div data-mh-noart>{current ? (current.album ?? current.title) : 'MusicHoarder'}</div>
          {/if}
          <div data-spectrum data-playing={isPlaying || undefined} aria-hidden="true">
            {#each { length: 16 }, i (i)}<i style:--i={i}></i>{/each}
          </div>
        </div>
      </Display>
    </div>

    <Lcd {tag} data-player-now>
      <strong>{current?.title ?? 'Pick something to play'}</strong>
      <span>{current?.artist ?? '—'}</span>
      <LcdDigits value={clock(position)} />
    </Lcd>

    <div data-player-cell data-wide>
      <ButtonGroup
        label="Transport"
        tone="primary"
        parts={[
          { label: 'Previous', icon: 'skip-back', onPress: onPrevious },
          {
            label: isPlaying ? 'Pause' : 'Play',
            icon: isPlaying ? 'pause' : 'play',
            onPress: onToggle
          },
          { label: 'Next', icon: 'skip-forward', onPress: onNext }
        ]}
      />
    </div>

    <div data-player-cell>
      <Latch
        label="Like"
        icon="heart"
        checked={liked}
        disabled={!canLike}
        onCheckedChange={onLike}
      />
      <small aria-hidden="true">Like</small>
    </div>
    <div data-player-cell>
      <Latch
        label="Mute"
        icon="volume-mute"
        checked={volume === 0}
        onCheckedChange={() => onMute()}
      />
      <small aria-hidden="true">Mute</small>
    </div>
    <div data-player-cell>
      <FlatKnob
        label="Volume"
        color="white"
        value={Math.round(volume * 100)}
        onValueChange={(v) => onVolume(v / 100)}
      />
      <small aria-hidden="true">Vol</small>
    </div>

    <div data-player-scrub>
      <Slider
        label="Position"
        value={Math.min(position, length || 0)}
        min={0}
        max={length || 1}
        step={0.5}
        disabled={!current || !length}
        format={clock}
        onValueChange={(v) => (scrub = v)}
        onValueCommit={(v) => {
          onSeek(v);
          scrub = null;
        }}
      />
    </div>
  </div>

  {#if queue.length > 0}
    <ItemList
      label="Queue"
      rows={queue}
      rowKey={(row) => row.key}
      value={currentKey}
      progress={length ? Math.min(1, position / length) : 0}
      onValueChange={(key) => {
        const i = queue.findIndex((t) => t.key === key);
        if (i >= 0) onPick(i);
      }}
      {columns}
    />
  {/if}
</Device>

<style>
  [data-hw-cover] {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    object-fit: cover;
  }
  [data-mh-artist] {
    overflow: hidden;
    text-overflow: ellipsis;
    color: var(--dim, #8a8a84);
  }
</style>
