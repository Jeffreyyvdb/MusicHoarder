import { describe, expect, test } from 'bun:test'
import { SIZES, type SizeName } from './config.ts'
import { availableBytes, FOOTPRINT_MB, limitsFor, memoryShortfall, requiredBytes } from './memory.ts'

const GB = 1024 ** 3

describe('memory check', () => {
  test('reads MemAvailable from /proc/meminfo', () => {
    const meminfo = 'MemTotal:       16000000 kB\nMemFree:          500000 kB\nMemAvailable:    3145728 kB\n'
    expect(availableBytes(meminfo)).toBe(3 * GB)
  })

  test('falls back to free memory when there is no MemAvailable', () => {
    expect(availableBytes('nothing here')).toBeGreaterThan(0)
  })

  test('larger libraries need more', () => {
    const sizes = Object.keys(SIZES) as SizeName[]
    for (let i = 1; i < sizes.length; i++)
      expect(requiredBytes(sizes[i]!, { build: false })).toBeGreaterThan(requiredBytes(sizes[i - 1]!, { build: false }))
  })

  test('the build counts when it would be the peak', () => {
    expect(requiredBytes('tiny', { build: true })).toBeGreaterThanOrEqual(requiredBytes('tiny', { build: false }))
    const f = FOOTPRINT_MB.tiny
    const stackMb = f.postgres + f.api + f.frontend + f.web
    expect(requiredBytes('tiny', { build: true })).toBe(Math.max(stackMb, f.build) * 1024 * 1024)
  })

  test('refuses below the threshold and says how to go on', () => {
    const needed = requiredBytes('medium', { build: false })
    expect(memoryShortfall('medium', needed, { build: false })).toBeNull()
    const message = memoryShortfall('medium', needed - 1, { build: false })
    expect(message).toContain('medium stack needs about')
    expect(message).toContain('--force')
  })

  test('limits sit above the measured peak', () => {
    const limits = limitsFor(1000)
    expect(limits.maxMb).toBe(1500)
    expect(limits.highMb).toBeLessThan(limits.maxMb)
    expect(limits.highMb).toBeGreaterThan(1000)
  })
})
