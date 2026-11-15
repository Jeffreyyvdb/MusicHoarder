// How much memory a stack takes, and whether the machine has it free before one starts.

import { readFileSync } from 'node:fs'
import { freemem } from 'node:os'
import type { SizeName } from './config.ts'

const MB = 1024 * 1024

/**
 * Peak memory per part of the stack, in MB, rounded up from cgroup `memory.peak` on real runs
 * (perf/README.md, "Memory"). `web` is Node plus headless Chromium during the browser scenarios.
 * `build` is the peak of `dotnet build` and the frontend's Vite build, which run before the stack.
 * tiny and small are measured; medium's Postgres is measured during its seed, and the rest of
 * medium and all of large are extrapolated — measure them when a machine has the room.
 */
export const FOOTPRINT_MB: Record<SizeName, { postgres: number; api: number; frontend: number; web: number; build: number }> = {
  tiny: { postgres: 256, api: 640, frontend: 128, web: 512, build: 2048 },
  small: { postgres: 320, api: 640, frontend: 128, web: 640, build: 2048 },
  medium: { postgres: 512, api: 1024, frontend: 192, web: 1024, build: 2048 },
  large: { postgres: 1024, api: 2048, frontend: 256, web: 1536, build: 2048 },
}

/** What `up`/`all` needs free before it starts: the whole stack at its peak, or the build if larger. */
export function requiredBytes(size: SizeName, options: { build: boolean }): number {
  const f = FOOTPRINT_MB[size]
  const stack = f.postgres + f.api + f.frontend + f.web
  return Math.max(stack, options.build ? f.build : 0) * MB
}

/** Memory a new process can get without swapping: `MemAvailable` on Linux, free memory elsewhere. */
export function availableBytes(meminfo?: string): number {
  const text = meminfo ?? (() => {
    try {
      return readFileSync('/proc/meminfo', 'utf8')
    } catch {
      return null
    }
  })()
  const match = text ? /^MemAvailable:\s+(\d+) kB/m.exec(text) : null
  return match ? Number(match[1]) * 1024 : freemem()
}

const gb = (bytes: number) => `${(bytes / 1024 ** 3).toFixed(1)} GB`

/** Null when there is room; otherwise why not, to show the user. */
export function memoryShortfall(size: SizeName, available: number, options: { build: boolean }): string | null {
  const needed = requiredBytes(size, options)
  if (available >= needed) return null
  return (
    `Only ${gb(available)} of memory is available, and a ${size} stack needs about ${gb(needed)}. ` +
    `Starting it anyway could push the machine into swap or get processes killed by the OOM killer. ` +
    `Wait for other work to finish, pick a smaller --size, or pass --force to start anyway.`
  )
}

/** Systemd and Docker memory properties for one part of the stack: MemoryMax at 1.5× its peak. */
export function limitsFor(peakMb: number) {
  const max = Math.ceil(peakMb * 1.5)
  return { maxMb: max, highMb: Math.ceil(max * 0.85), swapMaxMb: Math.ceil(max / 2) }
}
