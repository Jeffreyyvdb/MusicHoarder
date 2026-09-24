import { afterEach, describe, expect, it, vi } from 'vitest';
import { copyText } from './copy-text';

// The suite runs in node: no document, so the legacy fallback has nothing to copy with and must
// report failure rather than throw — which is exactly the plain-http case the helper exists for.
afterEach(() => {
  vi.unstubAllGlobals();
});

describe('copyText', () => {
  it('uses the async clipboard when there is one', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    expect(await copyText('docker compose pull')).toBe(true);
    expect(writeText).toHaveBeenCalledWith('docker compose pull');
  });

  it('says so instead of throwing when there is no clipboard (an insecure origin)', async () => {
    vi.stubGlobal('navigator', {});
    await expect(copyText('token')).resolves.toBe(false);
  });

  it('says so when the clipboard refuses', async () => {
    const writeText = vi.fn().mockRejectedValue(new DOMException('denied', 'NotAllowedError'));
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    await expect(copyText('token')).resolves.toBe(false);
  });
});
