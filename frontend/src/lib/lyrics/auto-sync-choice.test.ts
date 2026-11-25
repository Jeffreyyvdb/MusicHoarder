import { describe, expect, it } from 'vitest';
import { shouldAskAboutAutoSync } from '#lib/lyrics/auto-sync-choice.ts';

describe('shouldAskAboutAutoSync', () => {
  it('asks only while a provider is configured and nobody has answered', () => {
    expect(shouldAskAboutAutoSync({ enabled: true, autoSync: false, autoSyncChosen: false })).toBe(
      true
    );
    expect(shouldAskAboutAutoSync({ enabled: true, autoSync: false, autoSyncChosen: true })).toBe(
      false
    );
    expect(shouldAskAboutAutoSync({ enabled: false, autoSync: false, autoSyncChosen: false })).toBe(
      false
    );
  });

  it('never asks a server that does not report the choice', () => {
    expect(shouldAskAboutAutoSync({ enabled: true })).toBe(false);
  });
});
