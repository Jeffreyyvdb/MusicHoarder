// Opt-in OOM containment (Linux with a systemd user manager): each long-running part of the stack
// starts in its own transient scope, under a slice of `app.slice`, with a memory limit. Without it
// the API, the frontend and Chromium live in the cgroup of whatever started them — an editor, a
// terminal, an agent session — and when memory runs short, systemd-oomd (which kills whole cgroups)
// or the kernel kills that session along with the stack. In a scope of its own the stack is the
// one that goes. `PERF_SYSTEMD_SCOPE=1` or `--scope` turns it on; elsewhere it is a no-op.

import { spawnSync } from 'node:child_process'
import { limitsFor } from './memory.ts'

export interface ScopeRequest {
  /** Unit name, without `.scope`; made unique per start. */
  name: string
  /** Peak memory of this part, in MB (`FOOTPRINT_MB`). */
  peakMb: number
}

let available: boolean | null = null
let noticed = false

/** Whether `systemd-run --user` can start a scope here. */
export function systemdScopeAvailable(): boolean {
  if (available == null) {
    const probe = (args: string[]) => spawnSync(args[0]!, args.slice(1), { stdio: 'ignore' }).status === 0
    available = process.platform === 'linux' && probe(['systemd-run', '--version']) && probe(['systemctl', '--user', 'show', '--property=Version'])
  }
  return available
}

export function scopeRequested(flag: boolean, env: Record<string, string | undefined> = process.env): boolean {
  return flag || /^(1|true|yes|on)$/i.test(env.PERF_SYSTEMD_SCOPE ?? '')
}

/** Whether to use scopes for this run; says once why not when asked for and unavailable. */
export function useScopes(flag: boolean): boolean {
  if (!scopeRequested(flag)) return false
  if (systemdScopeAvailable()) return true
  if (!noticed) console.log('[stack] systemd scopes were asked for, but "systemd-run --user" is not available here; running without them.')
  noticed = true
  return false
}

export const slice = (env: Record<string, string | undefined> = process.env) => env.PERF_SYSTEMD_SLICE || 'app-mhperf.slice'

/** Unit names may not hold every character; keep them readable and unique per start. */
export function unitName(parts: string[]): string {
  const name = parts.filter(Boolean).join('-').replace(/[^A-Za-z0-9:_.-]/g, '_')
  return `${name}-${Date.now().toString(36)}${Math.floor(Math.random() * 36 ** 3).toString(36)}`
}

/**
 * The command line that runs `command` in a new scope. `systemd-run --scope` moves itself into the
 * scope and then execs the command, so the pid a caller gets back is the command's own, and the
 * environment and working directory are the caller's.
 */
export function scoped(command: string, args: string[], request: ScopeRequest): [string, string[]] {
  // One limit for every scope, in MB: for checking the containment itself with a deliberately low one.
  const override = Number(process.env.PERF_SYSTEMD_MEMORY_MAX_MB)
  const limits = override > 0 ? limitsFor(override / 1.5) : limitsFor(request.peakMb)
  return [
    'systemd-run',
    [
      '--user', '--scope', '--quiet', '--collect',
      `--slice=${slice()}`,
      `--unit=${request.name}`,
      '-p', `MemoryHigh=${limits.highMb}M`,
      '-p', `MemoryMax=${limits.maxMb}M`,
      '-p', `MemorySwapMax=${limits.swapMaxMb}M`,
      '--', command, ...args,
    ],
  ]
}

/** Stops scopes a stack left behind (best effort: they end by themselves when their processes do). */
export function stopScopes(units: string[]) {
  if (units.length === 0 || !systemdScopeAvailable()) return
  spawnSync('systemctl', ['--user', 'stop', ...units.map((u) => `${u}.scope`)], { stdio: 'ignore' })
}
