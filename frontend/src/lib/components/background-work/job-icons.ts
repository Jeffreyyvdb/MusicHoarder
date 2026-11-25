import type { Component } from 'svelte';
import {
  AudioLines,
  Captions,
  Clapperboard,
  DiscAlbum,
  Download,
  Gauge,
  Image,
  RefreshCw
} from '@lucide/svelte';
import type { BackgroundJobId } from '#lib/background-work/types.ts';

type Icon = Component<{ class?: string; strokeWidth?: number }>;

export const JOB_ICONS: Record<BackgroundJobId, Icon> = {
  lyrics: Captions,
  'transcription-allowance': AudioLines,
  'cover-art': Image,
  'music-videos': Clapperboard,
  'music-video-downloads': Download,
  'quality-grading': Gauge,
  'enrichment-retries': RefreshCw,
  'album-fill-follows': DiscAlbum
};
