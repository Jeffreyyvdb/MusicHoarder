import { describe, expect, it } from 'vitest';
import { cropMatte, matteScale } from './crop-matte';

// The cases are Android's VideoChildSizeTest's matte cases, case for case: the two clients fill
// the screen with the same picture.

// An iPhone in portrait, a 16:9 upload, and a 2.39:1 film in it (21 of the 160-row grid).
const PHONE = [402, 812] as const;
const FRAME = [1920, 1080] as const;
const LETTERBOX = 21 / 160;

describe('matteScale', () => {
  it('grows a letterboxed video until the picture fills a phone top to bottom', () => {
    const scale = matteScale(...PHONE, ...FRAME, { letterbox: LETTERBOX, pillarbox: 0 });
    expect(scale).toBeCloseTo(1.356, 3);
    // The picture — the frame less both bars — now spans the box's height exactly.
    const cover = Math.max(PHONE[0] / FRAME[0], PHONE[1] / FRAME[1]);
    expect(FRAME[1] * (1 - 2 * LETTERBOX) * cover * scale).toBeCloseTo(PHONE[1], 6);
  });

  it('leaves a pillarbox alone on a portrait phone, where the bars are already off screen', () => {
    expect(matteScale(...PHONE, ...FRAME, { letterbox: 0, pillarbox: 0.125 })).toBe(1);
  });

  it('crops a pillarbox on a wide window', () => {
    // A 4:3 clip in 16:9 behind a 1440 × 900 window: its picture has to reach the sides.
    expect(matteScale(1440, 900, ...FRAME, { letterbox: 0, pillarbox: 0.125 })).toBeCloseTo(1.2, 6);
  });

  it('crops whichever pair of bars needs more when both are baked in', () => {
    const both = matteScale(1440, 900, ...FRAME, { letterbox: LETTERBOX, pillarbox: 0.125 });
    const letterboxOnly = matteScale(1440, 900, ...FRAME, { letterbox: LETTERBOX, pillarbox: 0 });
    const pillarboxOnly = matteScale(1440, 900, ...FRAME, { letterbox: 0, pillarbox: 0.125 });
    expect(both).toBeCloseTo(Math.max(letterboxOnly, pillarboxOnly), 6);
  });

  it('is 1 for a full-frame picture or an unmeasured video', () => {
    expect(matteScale(...PHONE, ...FRAME, { letterbox: 0, pillarbox: 0 })).toBe(1);
    expect(matteScale(...PHONE, ...FRAME, { letterbox: null, pillarbox: null })).toBe(1);
    expect(matteScale(...PHONE, ...FRAME, {})).toBe(1);
  });

  it('is 1 until the box and the frame have a size', () => {
    expect(matteScale(0, 0, ...FRAME, { letterbox: LETTERBOX })).toBe(1);
    expect(matteScale(...PHONE, 0, 0, { letterbox: LETTERBOX })).toBe(1);
  });

  it('ignores a bar pair that would leave no picture', () => {
    expect(matteScale(...PHONE, ...FRAME, { letterbox: 0.5 })).toBe(1);
    expect(matteScale(...PHONE, ...FRAME, { letterbox: -0.1 })).toBe(1);
    expect(matteScale(...PHONE, ...FRAME, { letterbox: Number.NaN })).toBe(1);
  });
});

describe('cropMatte', () => {
  function videoNode(size: {
    clientWidth: number;
    clientHeight: number;
    videoWidth: number;
    videoHeight: number;
  }) {
    const listeners = new Map<string, () => void>();
    const node = {
      ...size,
      style: { transform: '' },
      addEventListener: (type: string, fn: () => void) => listeners.set(type, fn),
      removeEventListener: (type: string) => listeners.delete(type)
    };
    return { node, listeners };
  }

  it('scales the element once its frame size is known, and follows the measurement', () => {
    const { node, listeners } = videoNode({
      clientWidth: PHONE[0],
      clientHeight: PHONE[1],
      videoWidth: 0,
      videoHeight: 0
    });
    const action = cropMatte(node as unknown as HTMLVideoElement, { letterbox: LETTERBOX });
    expect(node.style.transform).toBe(''); // no frame yet

    node.videoWidth = FRAME[0];
    node.videoHeight = FRAME[1];
    listeners.get('loadedmetadata')?.();
    expect(node.style.transform).toMatch(/^scale\(1\.35\d+\)$/);

    action.update({ letterbox: 0, pillarbox: 0 });
    expect(node.style.transform).toBe('');

    action.update({ letterbox: LETTERBOX });
    action.destroy();
    expect(node.style.transform).toBe('');
    expect(listeners.size).toBe(0);
  });
});
