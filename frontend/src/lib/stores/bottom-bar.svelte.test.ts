import { describe, expect, it } from 'vitest';
import { bottomBar } from './bottom-bar.svelte';

/**
 * The bottom slot is claimed from effects, whose mount and cleanup order across two views is not
 * something to rely on — an Inbox detail replaced by the next one mounts the new toolbar before
 * the old one's cleanup runs. Claims are tokens precisely so any order ends in the right state.
 */
describe('bottomBar', () => {
  it('shows the tab bar until something claims the slot', () => {
    expect(bottomBar.kind).toBe('tabs');
    const release = bottomBar.claim('toolbar');
    expect(bottomBar.kind).toBe('toolbar');
    release();
    expect(bottomBar.kind).toBe('tabs');
  });

  it('lets the last claim win and restores the previous one on release', () => {
    const toolbar = bottomBar.claim('toolbar');
    const none = bottomBar.claim('none');
    expect(bottomBar.kind).toBe('none');
    none();
    expect(bottomBar.kind).toBe('toolbar');
    toolbar();
    expect(bottomBar.kind).toBe('tabs');
  });

  it('survives releases out of order', () => {
    const first = bottomBar.claim('toolbar');
    const second = bottomBar.claim('toolbar');
    first(); // the outgoing view cleans up after the incoming one has claimed
    expect(bottomBar.kind).toBe('toolbar');
    second();
    expect(bottomBar.kind).toBe('tabs');
  });

  it('treats a second release of the same claim as a no-op', () => {
    const outer = bottomBar.claim('none');
    const inner = bottomBar.claim('toolbar');
    inner();
    inner();
    expect(bottomBar.kind).toBe('none');
    outer();
    expect(bottomBar.kind).toBe('tabs');
  });
});
