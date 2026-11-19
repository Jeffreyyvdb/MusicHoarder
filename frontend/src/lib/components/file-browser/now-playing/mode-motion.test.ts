import { describe, expect, it } from 'vitest';
import { glideFrame } from './mode-motion.ts';

const box = (left: number, top: number, width: number, height: number) => ({
  left,
  top,
  width,
  height
});

describe('glideFrame', () => {
  it('puts the player back where it stood centred before it moved left', () => {
    const centred = box(480, 100, 490, 600);
    const left = box(40, 100, 490, 600);
    const aside = box(0, 80, 580, 700);
    expect(glideFrame(centred, left, aside)).toEqual({
      transform: 'translate(440px, 0px) scale(1)',
      transformOrigin: '285px 320px'
    });
  });

  it('scales a player that is drawn larger on its own', () => {
    const solo = box(400, 100, 560, 700);
    const split = box(40, 135, 400, 630);
    expect(glideFrame(solo, split, box(0, 0, 500, 900))?.transform).toBe(
      'translate(440px, 0px) scale(1.4)'
    );
  });

  it('stretches a monitor that learnt its clip is taller than it guessed', () => {
    const guessed = box(100, 145, 640, 360);
    const real = box(100, 85, 640, 480);
    expect(glideFrame(guessed, real, real, true)?.transform).toBe(
      'translate(0px, 0px) scale(1, 0.75)'
    );
  });

  it('does nothing when there was no box to move from', () => {
    expect(glideFrame(box(0, 0, 0, 0), box(10, 10, 300, 300), box(0, 0, 400, 400))).toBeNull();
  });

  it('does nothing when the layout change left the player where it was', () => {
    expect(
      glideFrame(box(10, 10, 300, 300), box(10.4, 10, 300, 300), box(0, 0, 400, 400))
    ).toBeNull();
  });
});
