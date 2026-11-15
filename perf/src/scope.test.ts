import { describe, expect, test } from 'bun:test'
import { scoped, scopeRequested, unitName } from './scope.ts'

describe('systemd scopes', () => {
  test('are opt-in', () => {
    expect(scopeRequested(false, {})).toBe(false)
    expect(scopeRequested(false, { PERF_SYSTEMD_SCOPE: '1' })).toBe(true)
    expect(scopeRequested(false, { PERF_SYSTEMD_SCOPE: '0' })).toBe(false)
    expect(scopeRequested(true, {})).toBe(true)
  })

  test('wrap the command in a user scope with memory limits, command last', () => {
    const [command, args] = scoped('dotnet', ['api.dll'], { name: 'mh-perf-tiny-api-x', peakMb: 600 })
    expect(command).toBe('systemd-run')
    expect(args.slice(0, 2)).toEqual(['--user', '--scope'])
    expect(args).toContain('--unit=mh-perf-tiny-api-x')
    expect(args).toContain('MemoryMax=900M')
    expect(args.slice(-3)).toEqual(['--', 'dotnet', 'api.dll'])
  })

  test('unit names are unique and safe', () => {
    const a = unitName(['mh-perf', '', 'tiny', 'api'])
    expect(a).toMatch(/^mh-perf-tiny-api-[0-9a-z]+$/)
    expect(unitName(['mh-perf', 'tiny', 'api'])).not.toBe(a)
  })
})
