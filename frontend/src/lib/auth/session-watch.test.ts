import { describe, expect, it, vi } from 'vitest';
import { classifySession, createSessionWatch, SESSION_RECHECK_MS } from './session-watch';

describe('classifySession', () => {
  it('reads a fresh /auth/me against the account the page was loaded for', () => {
    expect(classifySession('a', { id: 'a' })).toBe('same');
    expect(classifySession('a', { id: 'b' })).toBe('switched');
    expect(classifySession('a', null)).toBe('signed-out');
    // Could not ask is never "signed out".
    expect(classifySession('a', undefined)).toBe('unknown');
  });
});

function setup(check: () => Promise<{ id: string } | null>) {
  let now = 1_000;
  const onSignedOut = vi.fn();
  const onSwitched = vi.fn();
  const checkSpy = vi.fn(check);
  const watch = createSessionWatch({
    userId: () => 'a',
    check: checkSpy,
    onSignedOut,
    onSwitched,
    now: () => now
  });
  return {
    watch,
    check: checkSpy,
    onSignedOut,
    onSwitched,
    advance: (ms: number) => {
      now += ms;
    }
  };
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

describe('createSessionWatch', () => {
  it('leaves navigations alone for a minute after the page loaded', async () => {
    const t = setup(async () => ({ id: 'a' }));
    t.watch.poke();
    t.advance(SESSION_RECHECK_MS - 1);
    t.watch.poke();
    await settle();
    expect(t.check).not.toHaveBeenCalled();
  });

  it('checks at most once a minute, and not while a check is still out', async () => {
    let answer: (me: { id: string }) => void = () => {};
    const t = setup(() => new Promise((resolve) => (answer = resolve)));
    t.advance(SESSION_RECHECK_MS);
    t.watch.poke();
    t.watch.poke();
    expect(t.check).toHaveBeenCalledOnce();
    // Due again, but the first is still out: no second request piles up behind it.
    t.advance(SESSION_RECHECK_MS);
    t.watch.poke();
    expect(t.check).toHaveBeenCalledOnce();
    answer({ id: 'a' });
    await settle();
    t.watch.poke();
    expect(t.check).toHaveBeenCalledTimes(2);
    answer({ id: 'a' });
    await settle();
    t.watch.poke();
    expect(t.check).toHaveBeenCalledTimes(2);
    expect(t.onSignedOut).not.toHaveBeenCalled();
    expect(t.onSwitched).not.toHaveBeenCalled();
  });

  it('hands a 401 back to the server gate', async () => {
    const t = setup(async () => null);
    t.advance(SESSION_RECHECK_MS);
    t.watch.poke();
    await settle();
    expect(t.onSignedOut).toHaveBeenCalledOnce();
    expect(t.onSwitched).not.toHaveBeenCalled();
  });

  it('reloads when the session now belongs to another account', async () => {
    const t = setup(async () => ({ id: 'b' }));
    t.advance(SESSION_RECHECK_MS);
    t.watch.poke();
    await settle();
    expect(t.onSwitched).toHaveBeenCalledOnce();
    expect(t.onSignedOut).not.toHaveBeenCalled();
  });

  it('treats a failed check as "could not ask", not as signed out', async () => {
    const t = setup(async () => {
      throw new Error('auth/me failed: 503');
    });
    t.advance(SESSION_RECHECK_MS);
    t.watch.poke();
    await settle();
    expect(t.onSignedOut).not.toHaveBeenCalled();
    expect(t.onSwitched).not.toHaveBeenCalled();
  });
});
