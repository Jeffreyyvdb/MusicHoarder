import { describe, expect, it } from 'vitest';
import { isSubmitEnter } from './keyboard.ts';

function keydown(init: { key: string; isComposing?: boolean; keyCode?: number }): KeyboardEvent {
  return {
    key: init.key,
    isComposing: init.isComposing ?? false,
    keyCode: init.keyCode ?? (init.key === 'Enter' ? 13 : 0)
  } as KeyboardEvent;
}

describe('isSubmitEnter', () => {
  it('accepts a plain Enter', () => {
    expect(isSubmitEnter(keydown({ key: 'Enter' }))).toBe(true);
  });

  it('ignores the Enter that confirms an IME composition (Chromium, Firefox)', () => {
    expect(isSubmitEnter(keydown({ key: 'Enter', isComposing: true, keyCode: 229 }))).toBe(false);
  });

  it("ignores Safari's confirming Enter, which arrives with isComposing already false", () => {
    expect(isSubmitEnter(keydown({ key: 'Enter', isComposing: false, keyCode: 229 }))).toBe(false);
  });

  it('ignores other keys', () => {
    expect(isSubmitEnter(keydown({ key: 'a' }))).toBe(false);
  });
});
