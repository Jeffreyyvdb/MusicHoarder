// What changed between two runs. A difference only counts when it is larger than the spread
// either run showed on its own, so run-to-run noise is not reported as a win or a regression.

import type { RunResult } from './report.ts'
import { formatBytes, formatMs, type Summary } from './stats.ts'

interface Row {
  name: string
  before: string
  after: string
  change: string
  verdict: string
}

/** Faster or slower only beyond 10%, beyond the noise both samples carry, and beyond 5 ms. */
function judge(before: Summary, after: Summary): { change: string; verdict: string } {
  if (Number.isNaN(before.median) || Number.isNaN(after.median)) return { change: '–', verdict: '' }
  const delta = after.median - before.median
  const relative = before.median === 0 ? 0 : delta / before.median
  const noise = Math.max(before.median - before.min, after.median - after.min, 5)
  const change = `${delta >= 0 ? '+' : '−'}${formatMs(Math.abs(delta))} (${relative >= 0 ? '+' : '−'}${Math.abs(relative * 100).toFixed(0)}%)`
  if (Math.abs(relative) < 0.1 || Math.abs(delta) <= noise) return { change, verdict: 'same' }
  return { change, verdict: delta < 0 ? 'faster' : 'SLOWER' }
}

function table(title: string, rows: Row[]): string {
  if (!rows.length) return ''
  return [
    `### ${title}`, '',
    '| | Before | After | Change | |', '|---|--:|--:|--:|---|',
    ...rows.map((r) => `| ${r.name} | ${r.before} | ${r.after} | ${r.change} | ${r.verdict} |`),
    '',
  ].join('\n')
}

export function compare(before: RunResult, after: RunResult): string {
  const out = [
    `# ${before.label} → ${after.label}`, '',
    `Before: ${before.target}, commit ${before.commit}. After: ${after.target}, commit ${after.commit}.`, '',
  ]
  if (before.target !== after.target) out.push('The two runs measured different targets, so read the differences with that in mind.', '')

  const apiRows: Row[] = []
  const sizeRows: Row[] = []
  for (const b of before.api?.endpoints ?? []) {
    const a = after.api?.endpoints.find((e) => e.name === b.name)
    if (!a) continue
    apiRows.push({ name: b.name, before: formatMs(b.total.median), after: formatMs(a.total.median), ...judge(b.total, a.total) })
    const bytes = a.decodedBytes - b.decodedBytes
    const queries = (a.db?.queriesPerRequest ?? 0) - (b.db?.queriesPerRequest ?? 0)
    if (Math.abs(bytes) > Math.max(1024, b.decodedBytes * 0.05) || Math.abs(queries) >= 1)
      sizeRows.push({
        name: b.name,
        before: `${formatBytes(b.decodedBytes)} · ${Math.round(b.db?.queriesPerRequest ?? 0)} SQL`,
        after: `${formatBytes(a.decodedBytes)} · ${Math.round(a.db?.queriesPerRequest ?? 0)} SQL`,
        change: `${bytes >= 0 ? '+' : '−'}${formatBytes(Math.abs(bytes))}${queries ? `, ${queries > 0 ? '+' : '−'}${Math.abs(Math.round(queries))} SQL` : ''}`,
        verdict: bytes < 0 || queries < 0 ? 'lighter' : 'HEAVIER',
      })
  }
  out.push(table('API, median time', apiRows), table('API, payload and SQL statements', sizeRows))

  const webRows: Row[] = []
  for (const b of before.web?.scenarios ?? []) {
    const a = after.web?.scenarios.find((s) => s.name === b.name && s.profile === b.profile)
    if (!a) continue
    webRows.push({ name: `${b.profile} · ${b.name}`, before: formatMs(b.primary.median), after: formatMs(a.primary.median), ...judge(b.primary, a.primary) })
  }
  out.push(table('Web app', webRows))

  const all = [...apiRows, ...webRows]
  out.push(`${all.filter((r) => r.verdict === 'faster').length} faster, ${all.filter((r) => r.verdict === 'SLOWER').length} slower, ${all.filter((r) => r.verdict === 'same').length} unchanged.`)
  return out.filter(Boolean).join('\n\n')
}
