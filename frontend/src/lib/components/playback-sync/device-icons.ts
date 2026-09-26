import { Laptop, Smartphone, Speaker, Tablet } from '@lucide/svelte';
import type { Component } from 'svelte';
import type { DeviceKind } from '$lib/playback-sync/wire';

/** The glyph for a device in the picker and on the "Playing on …" lines. */
export const DEVICE_ICONS: Record<DeviceKind, Component<{ class?: string }>> = {
  computer: Laptop,
  phone: Smartphone,
  tablet: Tablet,
  unknown: Speaker
};
