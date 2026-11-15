// One local stack per machine. A Postgres, a Release API, the Node frontend and a headless
// Chromium together take a few GB; several sessions each starting one at once ran a 15 GB machine
// out of memory. `up` and `all` take this lock before they start anything, and wait for it.
//
// The lock is a file (`stack.lock` in the shared perf cache folder) holding JSON: who took it, and
// the processes that keep it alive. It is held while any of them runs. During `all` that is the CLI
// itself; after `up` it is the API and the frontend, which outlive the CLI, so the lock stays taken
// until `down` stops them. A holder whose processes have all gone (a crash, a reboot, a kill -9)
// is stale, and the next taker removes it.

import { dlopen, FFIType } from 'bun:ffi'
import { closeSync, mkdirSync, openSync, readFileSync, renameSync, statSync, unlinkSync, writeFileSync } from 'node:fs'
import { dirname } from 'node:path'

export interface ProcessRef {
  pid: number
  /** When the process started (Linux: clock ticks since boot), so a reused pid is not mistaken for it. */
  start: string | null
}

export interface LockHolder {
  /** The CLI that took the lock; null once `up` has handed it to the stack's processes. */
  owner: ProcessRef | null
  /** The stack's long-running processes, once started. */
  stack: ProcessRef[]
  command: string
  workDir: string
  size: string
  startedAt: string
}

/** Start time of a process, from /proc on Linux; null elsewhere or when it is gone. */
export function processStart(pid: number): string | null {
  try {
    const stat = readFileSync(`/proc/${pid}/stat`, 'utf8')
    return stat.slice(stat.lastIndexOf(')') + 2).split(' ')[19] ?? null
  } catch {
    return null
  }
}

export const ref = (pid: number): ProcessRef => ({ pid, start: processStart(pid) })

export function running(proc: ProcessRef): boolean {
  try {
    process.kill(proc.pid, 0)
  } catch (error) {
    // EPERM: it exists, under another user.
    if ((error as NodeJS.ErrnoException).code !== 'EPERM') return false
  }
  return proc.start == null || processStart(proc.pid) == null || processStart(proc.pid) === proc.start
}

export const holderAlive = (holder: LockHolder) => [holder.owner, ...holder.stack].some((p) => p != null && running(p))

const ownedByMe = (holder: LockHolder | null) => holder?.owner?.pid === process.pid

export function describeHolder(holder: LockHolder): string {
  const owner = holder.owner && running(holder.owner) ? `pid ${holder.owner.pid}` : null
  const stack = holder.stack.filter(running).map((p) => p.pid)
  const who = owner
    ? `"bun perf ${holder.command}" (${owner})`
    : `a stack left running by "bun perf ${holder.command}" (pids ${stack.join(', ')}; "bun perf down --size ${holder.size}" with that work dir stops it)`
  return `${who}, ${holder.size} library in ${holder.workDir}, since ${holder.startedAt}`
}

/** A file whose JSON is not readable yet: written by a taker a moment ago, or left half-written. */
const UNREADABLE_GRACE_MS = 5_000

function read(path: string): { text: string; holder: LockHolder | null; ageMs: number } | null {
  try {
    const text = readFileSync(path, 'utf8')
    const ageMs = Date.now() - statSync(path).mtimeMs
    try {
      return { text, holder: JSON.parse(text) as LockHolder, ageMs }
    } catch {
      return { text, holder: null, ageMs }
    }
  } catch {
    return null
  }
}

export function readLock(path: string): LockHolder | null {
  return read(path)?.holder ?? null
}

/** flock(2) from libc: a kernel lock, released when its holder exits however it exits. */
let libc: { flock: (fd: number, operation: number) => number } | null = null
function flock(fd: number, operation: number) {
  libc ??= dlopen(process.platform === 'darwin' ? 'libc.dylib' : 'libc.so.6', {
    flock: { args: [FFIType.i32, FFIType.i32], returns: FFIType.i32 },
  }).symbols
  // A non-zero result is almost always EINTR, which a retry answers.
  for (let attempt = 0; libc.flock(fd, operation) !== 0; attempt++)
    if (attempt > 3) throw new Error(`flock(${operation}) failed`)
}

const LOCK_EX = 2
const LOCK_UN = 8

/**
 * Every change to an existing lock file (removing a stale one, releasing, updating) happens under
 * a kernel lock on a guard file, so two sessions that both find the same stale lock cannot remove
 * the fresh one the other has just written. It is held for microseconds and needs no recovery: the
 * kernel drops it when its holder dies. Taking a free lock needs no guard: `wx` is atomic.
 */
function withGuard<T>(path: string, body: () => T): T {
  const fd = openSync(`${path}.guard`, 'a')
  try {
    flock(fd, LOCK_EX)
    try {
      return body()
    } finally {
      flock(fd, LOCK_UN)
    }
  } finally {
    closeSync(fd)
  }
}

export interface AcquireOptions {
  /** Wait for the holder to finish (default), or fail at once. */
  wait: boolean
  /** A live holder this taker may replace anyway (its own left-running stack, which `up` restarts). */
  takeOver?: (holder: LockHolder) => boolean
  /** Called when the lock is busy, again whenever the holder changes. */
  onBusy?: (holder: LockHolder | null) => void
  pollMs?: number
}

export class LockBusyError extends Error {
  constructor(readonly holder: LockHolder | null) {
    super(`Another perf stack is running on this machine: ${holder ? describeHolder(holder) : 'unknown holder'}.`)
  }
}

/** One attempt: takes the lock if it is free, stale, or takeable; otherwise returns its holder. */
export function tryAcquire(path: string, me: LockHolder, takeOver?: (holder: LockHolder) => boolean): { ok: true } | { ok: false; holder: LockHolder | null } {
  mkdirSync(dirname(path), { recursive: true })
  for (;;) {
    try {
      const fd = openSync(path, 'wx', 0o644)
      writeFileSync(fd, JSON.stringify(me, null, 2))
      closeSync(fd)
      return { ok: true }
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'EEXIST') throw error
    }
    const seen = read(path)
    if (!seen) continue // released between the two calls
    const removable = seen.holder
      ? !holderAlive(seen.holder) || (takeOver?.(seen.holder) ?? false)
      : seen.ageMs > UNREADABLE_GRACE_MS
    if (!removable) return { ok: false, holder: seen.holder }
    withGuard(path, () => {
      if (read(path)?.text === seen.text) unlinkSync(path)
    })
  }
}

export async function acquire(path: string, me: LockHolder, options: AcquireOptions): Promise<void> {
  let last: string | undefined
  for (;;) {
    const result = tryAcquire(path, me, options.takeOver)
    if (result.ok) return
    if (!options.wait) throw new LockBusyError(result.holder)
    const key = JSON.stringify(result.holder)
    if (key !== last) options.onBusy?.(result.holder)
    last = key
    await new Promise((resolve) => setTimeout(resolve, options.pollMs ?? 2_000))
  }
}

/** Rewrites the lock this process holds, e.g. to add the stack's processes or hand it over. */
export function updateLock(path: string, change: (holder: LockHolder) => LockHolder): boolean {
  return withGuard(path, () => {
    const holder = readLock(path)
    if (!ownedByMe(holder)) return false
    const tmp = `${path}.${process.pid}.tmp`
    writeFileSync(tmp, JSON.stringify(change(holder!), null, 2))
    renameSync(tmp, path)
    return true
  })
}

/** Removes the lock when `matches` says it is this caller's to release. */
export function releaseLock(path: string, matches: (holder: LockHolder) => boolean): boolean {
  return withGuard(path, () => {
    const holder = readLock(path)
    if (!holder || !matches(holder)) return false
    unlinkSync(path)
    return true
  })
}

/** Releases the lock if this process took it and still owns it (not after `up` handed it over). */
export const releaseMine = (path: string) => releaseLock(path, (holder) => ownedByMe(holder))
