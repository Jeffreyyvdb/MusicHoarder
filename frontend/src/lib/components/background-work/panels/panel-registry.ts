import type { Component } from 'svelte';
import type { BackgroundPanel } from '#lib/background-work/types.ts';
import BarsPanel from './BarsPanel.svelte';
import BreakdownPanel from './BreakdownPanel.svelte';
import FiguresPanel from './FiguresPanel.svelte';
import LibraryMapPanel from './LibraryMapPanel.svelte';
import SeriesPanel from './SeriesPanel.svelte';

export type PanelKind = BackgroundPanel['kind'];
type PanelOf<K extends PanelKind> = Extract<BackgroundPanel, { kind: K }>;

/**
 * The view for each kind of panel. A kind the API adds without a view here fails to compile, which
 * is the point of a record over an if-chain.
 */
export const PANEL_VIEWS: { [K in PanelKind]: Component<{ panel: PanelOf<K>; now?: number }> } = {
  breakdown: BreakdownPanel,
  series: SeriesPanel,
  'library-map': LibraryMapPanel,
  bars: BarsPanel,
  figures: FiguresPanel
};
