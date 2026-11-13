// Turns a run into something a person reads: a Markdown summary for the terminal and pull
// requests, and a self-contained HTML page with the detail behind every number.

import type { ApiReport, EndpointResult } from './api/bench.ts'
import { formatBytes, formatMs } from './stats.ts'
import type { ScenarioResult, WebReport } from './web/bench.ts'

export interface RunResult {
  label: string
  target: string
  commit: string
  /** Whether the checkout had uncommitted changes outside perf/ when measured. */
  dirty: boolean
  startedAt: string
  api: ApiReport | null
  web: WebReport | null
}

const BYTE_METRICS = /^(Transferred|JS heap)/
const COUNT_METRICS = /^(Requests|API calls|Cover requests|DOM nodes)$/

export function formatMetric(name: string, value: number | null): string {
  if (value == null) return '–'
  if (BYTE_METRICS.test(name)) return formatBytes(value)
  if (COUNT_METRICS.test(name)) return Math.round(value).toLocaleString('en')
  if (name.startsWith('Layout shift')) return value.toFixed(3)
  if (name.startsWith('Slow frames')) return `${value.toFixed(0)}%`
  return formatMs(value)
}

const verdict = (over: string[], error?: string) => (error ? `failed: ${error}` : over.length ? `over (${over.join('; ')})` : 'ok')

function groups<T extends { group?: string; profile?: string }>(items: T[], key: (item: T) => string): [string, T[]][] {
  const out = new Map<string, T[]>()
  for (const item of items) out.set(key(item), [...(out.get(key(item)) ?? []), item])
  return [...out.entries()]
}

/** The findings worth reading first, worst first: what is over budget and why it is. */
export function findings(run: RunResult): string[] {
  const out: string[] = []
  for (const e of run.api?.endpoints ?? []) {
    if (e.error) out.push(`**${e.name}** failed (${e.error}).`)
    else if (e.overBudget.length) {
      const why: string[] = []
      const top = e.fields[0]
      if (top && top.share > 0.25) why.push(`${Math.round(top.share * 100)}% of the body is \`${top.field}\``)
      if (e.db && e.db.queriesPerRequest >= 15) why.push(`${Math.round(e.db.queriesPerRequest)} SQL statements per request`)
      if (e.db && e.db.msPerRequest > e.total.median * 0.5) why.push(`${formatMs(e.db.msPerRequest)} of it in the database`)
      out.push(`**${e.name}** (\`${e.path.split('?')[0]}\`): ${e.overBudget.join('; ')}${why.length ? ` — ${why.join(', ')}` : ''}.`)
    }
  }
  const scenarios = run.web?.scenarios ?? []
  for (const s of scenarios.filter((x) => x.error)) out.push(`**${s.name}** on ${s.profile} failed (${s.error}).`)
  // Cold starts usually miss their budget together and for one reason; say it once per profile.
  for (const [profile, loads] of groups(scenarios.filter((s) => s.kind === 'load' && !s.error && s.overBudget.length), (s) => s.profile)) {
    const times = loads.map((s) => s.primary.median)
    const slowest = loads.flatMap((s) => s.api).sort((x, y) => y.durationMs - x.durationMs)[0]
    out.push(
      `**Cold starts** on ${profile}: ${loads.length === 1 ? loads[0]!.name : `${loads.length} pages`} take ${formatMs(Math.min(...times))}` +
        `${loads.length > 1 ? `–${formatMs(Math.max(...times))}` : ''} to become usable, over the ${formatMs(loads[0]!.budgetMs)} budget` +
        (slowest ? ` — slowest call \`${slowest.path}\` took ${formatMs(slowest.durationMs)} for ${formatBytes(slowest.transferBytes)}` : '') + '.',
    )
  }
  for (const s of scenarios.filter((x) => x.kind === 'interaction' && !x.error && x.overBudget.length)) {
    const busy = s.metrics['Main thread busy']
    out.push(
      `**${s.name}** on ${s.profile}: ${formatMs(s.primary.median)}, over the ${formatMs(s.budgetMs)} budget` +
        (busy != null && busy > s.primary.median * 0.4 ? ` — ${formatMs(busy)} of it is the main thread working, not the network` : '') + '.',
    )
  }
  // The same large response fetched twice while one page opens is waste whatever the budget says.
  const repeated = new Map<string, { count: number; bytes: number; where: string[] }>()
  for (const s of run.web?.scenarios ?? []) {
    for (const r of s.repeats ?? []) {
      if (r.transferBytes < 200_000) continue
      const entry = repeated.get(r.path) ?? { count: r.count, bytes: r.transferBytes, where: [] }
      entry.where.push(`${s.name} on ${s.profile}`)
      repeated.set(r.path, entry)
    }
  }
  for (const [path, r] of repeated)
    out.push(`\`${path}\` is fetched ${r.count} times while a page opens (${formatBytes(r.bytes)} in total): ${r.where.slice(0, 3).join(', ')}${r.where.length > 3 ? '…' : ''}.`)
  // What matters is what reaches the browser: when the frontend proxy compresses, an API that
  // sends plain JSON to it over loopback is fine.
  const proxyCompresses = (run.api?.endpoints ?? []).some((e) => e.proxy?.contentEncoding)
  const uncompressed = (run.api?.endpoints ?? []).filter((e) => !e.contentEncoding && e.gzipBytes != null && e.decodedBytes > 50_000)
  if (uncompressed.length && !proxyCompresses)
    out.push(
      `${uncompressed.length} JSON responses over 50 kB leave the server uncompressed (${uncompressed.map((e) => e.name).slice(0, 4).join(', ')}…). ` +
        'Unless a proxy in front compresses them, that is the size on the wire.',
    )
  return out
}

function apiMarkdown(api: ApiReport): string {
  const lines: string[] = ['## API', '', `${api.songs?.toLocaleString('en') ?? '?'} songs in the library. Times are for one caller, called directly; "×8" is eight callers at once.`, '']
  for (const [group, endpoints] of groups(api.endpoints, (e) => e.group)) {
    lines.push(`### ${group}`, '', '| Request | Median | p95 | Size | Gzipped | SQL | CPU | ×8 p95 | Budget |', '|---|--:|--:|--:|--:|--:|--:|--:|---|')
    for (const e of endpoints) {
      lines.push(
        `| ${e.name} | ${formatMs(e.total.median)} | ${formatMs(e.total.p95)} | ${formatBytes(e.decodedBytes)} | ${formatBytes(e.gzipBytes)} | ` +
          `${e.db ? `${Math.round(e.db.queriesPerRequest)} · ${formatMs(e.db.msPerRequest)}` : '–'} | ${formatMs(e.cpuMsPerRequest)} | ` +
          `${e.load ? formatMs(e.load.total.p95) : '–'} | ${verdict(e.overBudget, e.error)} |`,
      )
    }
    lines.push('')
  }
  const heavy = api.endpoints.filter((e) => e.fields.length && e.decodedBytes > 200_000)
  if (heavy.length) {
    lines.push('### Where the bytes go', '')
    for (const e of heavy)
      lines.push(`- **${e.name}** (${formatBytes(e.decodedBytes)}, ${e.rows?.toLocaleString('en')} rows): ` + e.fields.slice(0, 5).map((f) => `\`${f.field}\` ${Math.round(f.share * 100)}%`).join(', '))
    lines.push('')
  }
  const proxied = api.endpoints.filter((e) => e.proxy)
  if (proxied.length) {
    lines.push('### Through the frontend proxy', '', '| Request | Direct | Via /api/mh | Added | Sent to the browser |', '|---|--:|--:|--:|--:|')
    for (const e of proxied)
      lines.push(
        `| ${e.name} | ${formatMs(e.total.median)} | ${formatMs(e.proxy!.total.median)} | ${formatMs(e.proxy!.total.median - e.total.median)} | ` +
          `${e.proxy!.wireBytes != null ? `${formatBytes(e.proxy!.wireBytes)}${e.proxy!.contentEncoding ? ` (${e.proxy!.contentEncoding})` : ''}` : '–'} |`,
      )
    lines.push('')
  }
  return lines.join('\n')
}

const LOAD_COLUMNS = ['First paint (FCP)', 'Main thread blocked before usable', 'Transferred, total', 'Transferred, API', 'API calls', 'JS heap']

function webMarkdown(web: WebReport): string {
  const lines: string[] = ['## Web app', '']
  for (const [profile, scenarios] of groups(web.scenarios, (s) => s.profile)) {
    lines.push(`### ${profile}`, '', web.profiles[profile] ?? '', '')
    const loads = scenarios.filter((s) => s.kind === 'load')
    if (loads.length) {
      lines.push(`| Cold start | Usable | ${LOAD_COLUMNS.map((c) => c.replace('Main thread blocked before usable', 'Blocked').replace('Transferred, ', '').replace('total', 'Transferred').replace(/^API$/, 'of which API')).join(' | ')} | Budget |`)
      lines.push(`|---|--:|${LOAD_COLUMNS.map(() => '--:').join('|')}|---|`)
      for (const s of loads)
        lines.push(`| ${s.name} | ${formatMs(s.primary.median)} | ${LOAD_COLUMNS.map((c) => formatMetric(c, s.metrics[c] ?? null)).join(' | ')} | ${verdict(s.overBudget, s.error)} |`)
      lines.push('')
    }
    const interactions = scenarios.filter((s) => s.kind === 'interaction')
    if (interactions.length) {
      lines.push('| Interaction | Median | Worst | Detail | Budget |', '|---|--:|--:|---|---|')
      for (const s of interactions)
        lines.push(
          `| ${s.name} | ${formatMs(s.primary.median)} | ${formatMs(s.primary.max)} | ` +
            `${Object.entries(s.metrics).map(([k, v]) => `${k} ${formatMetric(k, v)}`).join(', ') || '–'} | ${verdict(s.overBudget, s.error)} |`,
        )
      lines.push('')
    }
  }
  return lines.join('\n')
}

export function renderMarkdown(run: RunResult): string {
  const lines = [
    `# Performance run: ${run.label}`,
    '',
    `${run.target} · commit ${run.commit || '?'}${run.dirty ? ' with uncommitted changes' : ''} · ${run.startedAt.slice(0, 16).replace('T', ' ')} UTC`,
    '',
  ]
  const top = findings(run)
  lines.push('## Findings', '', ...(top.length ? top.map((f) => `- ${f}`) : ['Everything measured is within its budget.']), '')
  if (run.api) lines.push(apiMarkdown(run.api))
  if (run.web) lines.push(webMarkdown(run.web))
  return lines.join('\n')
}

// ---------------------------------------------------------------------------------------------

const esc = (text: string) => text.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!)

/** Renders the inline emphasis the findings use. */
const inline = (text: string) => esc(text).replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>').replace(/`(.+?)`/g, '<code>$1</code>')

function bar(value: number, budget: number | null | undefined, max: number): string {
  const width = Math.max(1, Math.min(100, (value / max) * 100))
  const over = budget != null && value > budget
  const mark = budget != null && budget < max ? `<i class="budget" style="left:${(budget / max) * 100}%"></i>` : ''
  return `<span class="bar"><b class="${over ? 'over' : ''}" style="width:${width}%"></b>${mark}</span>`
}

const status = (over: string[], error?: string) =>
  error ? `<span class="tag fail">failed</span>` : over.length ? `<span class="tag over">over budget</span>` : `<span class="tag ok">ok</span>`

function endpointRow(e: EndpointResult, max: number): string {
  const detail: string[] = []
  if (e.error) detail.push(`<p>${esc(e.error)}</p>`)
  detail.push(
    `<p><code>${esc(e.path)}</code> · HTTP ${e.status} · headers after ${formatMs(e.ttfb.median)} · ` +
      `${e.contentEncoding ? `sent as ${e.contentEncoding}` : 'sent uncompressed'} · cache-control: ${esc(e.cacheControl ?? 'none')}</p>`,
  )
  if (e.fields.length)
    detail.push(
      `<p>${e.rows?.toLocaleString('en')} rows. Bytes by field:</p><table class="mini">` +
        e.fields.map((f) => `<tr><td><code>${esc(f.field)}</code></td><td class="n">${formatBytes(f.bytes)}</td><td class="n">${Math.round(f.share * 100)}%</td><td>${bar(f.share, null, 1)}</td></tr>`).join('') +
        '</table>',
    )
  if (e.db?.top.length)
    detail.push(
      `<p>${e.db.queriesPerRequest.toFixed(1)} SQL statements per request, ${formatMs(e.db.msPerRequest)} in the database. Costliest:</p><table class="mini">` +
        e.db.top.map((s) => `<tr><td class="n">${s.callsPerRequest.toFixed(1)}×</td><td class="n">${formatMs(s.msPerRequest)}</td><td class="n">${Math.round(s.rowsPerRequest).toLocaleString('en')} rows</td><td><code>${esc(s.sql)}</code></td></tr>`).join('') +
        '</table>',
    )
  if (e.load)
    detail.push(`<p>With ${e.load.concurrency} callers at once: ${e.load.requestsPerSecond.toFixed(0)} requests/s, median ${formatMs(e.load.total.median)}, p95 ${formatMs(e.load.total.p95)}${e.load.errors ? `, ${e.load.errors} errors` : ''}.</p>`)
  if (e.proxy)
    detail.push(`<p>Through the frontend proxy: median ${formatMs(e.proxy.total.median)} (${formatMs(e.proxy.total.median - e.total.median)} added), headers after ${formatMs(e.proxy.ttfb.median)}${e.proxy.wireBytes != null ? `, ${formatBytes(e.proxy.wireBytes)} sent${e.proxy.contentEncoding ? ` as ${e.proxy.contentEncoding}` : ' uncompressed'}` : ''}.</p>`)
  if (e.overBudget.length) detail.push(`<p class="overtext">${esc(e.overBudget.join('; '))}</p>`)

  return `<tr>
    <td><details><summary>${esc(e.name)}</summary><div class="detail">${detail.join('')}</div></details></td>
    <td class="n">${formatMs(e.total.median)}</td><td class="n">${formatMs(e.total.p95)}</td>
    <td class="w">${Number.isNaN(e.total.p95) ? '' : bar(e.total.p95, e.budget.p95Ms, max)}</td>
    <td class="n">${formatBytes(e.decodedBytes)}</td><td class="n">${formatBytes(e.gzipBytes)}</td>
    <td class="n">${e.db ? Math.round(e.db.queriesPerRequest) : '–'}</td><td class="n">${e.db ? formatMs(e.db.msPerRequest) : '–'}</td>
    <td class="n">${formatMs(e.cpuMsPerRequest)}</td><td class="n">${e.load ? formatMs(e.load.total.p95) : '–'}</td>
    <td>${status(e.overBudget, e.error)}</td></tr>`
}

function scenarioRow(s: ScenarioResult, max: number): string {
  const metrics = Object.entries(s.metrics)
    .map(([name, value]) => `<tr><td>${esc(name)}</td><td class="n">${formatMetric(name, value)}</td></tr>`)
    .join('')
  const calls = s.api.length
    ? `<p>Slowest API calls:</p><table class="mini"><tr><th>Call</th><th class="n">Started</th><th class="n">Took</th><th class="n">Headers</th><th class="n">On the wire</th><th class="n">Decoded</th></tr>` +
      s.api.map((c) => `<tr><td><code>${esc(c.path)}</code></td><td class="n">${formatMs(c.startMs)}</td><td class="n">${formatMs(c.durationMs)}</td><td class="n">${formatMs(c.ttfbMs)}</td><td class="n">${formatBytes(c.transferBytes)}</td><td class="n">${formatBytes(c.decodedBytes)}</td></tr>`).join('') +
      '</table>'
    : ''
  const repeats = s.repeats?.length
    ? `<p>Requested more than once: ${s.repeats.map((r) => `<code>${esc(r.path)}</code> ×${r.count} (${formatBytes(r.transferBytes)})`).join(', ')}</p>`
    : ''
  return `<tr>
    <td><details><summary>${esc(s.name)}</summary><div class="detail">${s.error ? `<p>${esc(s.error)}</p>` : ''}<table class="mini">${metrics}</table>${calls}${repeats}</div></details></td>
    <td class="n">${formatMs(s.primary.median)}</td><td class="n">${formatMs(s.primary.min)}</td><td class="n">${formatMs(s.primary.max)}</td>
    <td class="w">${Number.isNaN(s.primary.median) ? '' : bar(s.primary.median, s.budgetMs, max)}</td>
    <td class="n">${s.budgetMs != null ? formatMs(s.budgetMs) : '–'}</td>
    <td>${status(s.overBudget, s.error)}</td></tr>`
}

const scale = (values: number[], budgets: (number | null | undefined)[]) =>
  Math.max(1, ...values.filter((v) => !Number.isNaN(v)), ...budgets.filter((b): b is number => b != null)) * 1.05

export function renderHtml(run: RunResult): string {
  const sections: string[] = []
  const top = findings(run)
  sections.push(`<section><h2>Findings</h2>${top.length ? `<ul>${top.map((f) => `<li>${inline(f)}</li>`).join('')}</ul>` : '<p>Everything measured is within its budget.</p>'}</section>`)

  if (run.api) {
    for (const [group, endpoints] of groups(run.api.endpoints, (e) => e.group)) {
      const max = scale(endpoints.map((e) => e.total.p95), endpoints.map((e) => e.budget.p95Ms))
      sections.push(`<section><h2>API · ${esc(group)}</h2>
        <div class="scroll"><table><thead><tr><th>Request</th><th class="n">Median</th><th class="n">p95</th><th>p95 against budget</th><th class="n">Size</th><th class="n">Gzipped</th><th class="n">SQL</th><th class="n">DB time</th><th class="n">CPU</th><th class="n">×8 p95</th><th></th></tr></thead>
        <tbody>${endpoints.map((e) => endpointRow(e, max)).join('')}</tbody></table></div></section>`)
    }
  }
  if (run.web) {
    for (const [profile, scenarios] of groups(run.web.scenarios, (s) => s.profile)) {
      for (const kind of ['load', 'interaction'] as const) {
        const rows = scenarios.filter((s) => s.kind === kind)
        if (!rows.length) continue
        const max = scale(rows.map((s) => s.primary.median), rows.map((s) => s.budgetMs))
        sections.push(`<section><h2>Web · ${esc(profile)} · ${kind === 'load' ? 'cold starts' : 'interactions'}</h2>
          <p class="note">${esc(run.web.profiles[profile] ?? '')}</p>
          <div class="scroll"><table><thead><tr><th>${kind === 'load' ? 'Page, until usable' : 'Interaction'}</th><th class="n">Median</th><th class="n">Best</th><th class="n">Worst</th><th>Median against budget</th><th class="n">Budget</th><th></th></tr></thead>
          <tbody>${rows.map((s) => scenarioRow(s, max)).join('')}</tbody></table></div></section>`)
      }
    }
  }

  return `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Performance: ${esc(run.label)}</title>
<style>
  :root { --bg:#fff; --fg:#1c1c1e; --dim:#636366; --line:#e5e5ea; --fill:#f2f2f7; --bar:#3478f6; --over:#d70015; --ok:#248a3d; --warn:#b25000; }
  @media (prefers-color-scheme: dark) { :root { --bg:#000; --fg:#f2f2f7; --dim:#98989f; --line:#2c2c2e; --fill:#1c1c1e; --bar:#409cff; --over:#ff6961; --ok:#30d158; --warn:#ffb340; } }
  * { box-sizing: border-box; }
  body { margin:0; padding:24px 16px 64px; background:var(--bg); color:var(--fg); font:15px/1.45 system-ui,-apple-system,"Segoe UI",sans-serif; }
  main { max-width:1100px; margin:0 auto; }
  h1 { font-size:26px; margin:0 0 4px; } h2 { font-size:18px; margin:36px 0 10px; }
  .note, header p { color:var(--dim); margin:0 0 10px; }
  code { font:12.5px ui-monospace,SFMono-Regular,Menlo,monospace; overflow-wrap:anywhere; }
  .scroll { overflow-x:auto; } table { border-collapse:collapse; width:100%; }
  th { text-align:left; font-weight:600; font-size:12.5px; color:var(--dim); padding:6px 10px; border-bottom:1px solid var(--line); white-space:nowrap; }
  td { padding:8px 10px; border-bottom:1px solid var(--line); vertical-align:top; }
  .n { text-align:right; font-variant-numeric:tabular-nums; white-space:nowrap; } .w { min-width:160px; }
  /* The detail opens under the whole row, not inside the first column: the cell stays narrow and lets it overflow. */
  td:first-child { max-width:300px; } summary { cursor:pointer; }
  .detail { margin:8px 0 4px; color:var(--dim); font-size:13.5px; width:min(1040px, calc(100vw - 52px)); } .detail p { margin:6px 0; }
  .mini { width:auto; } .mini td, .mini th { padding:2px 12px 2px 0; border:0; font-size:13px; } .mini td:last-child { min-width:120px; }
  .bar { position:relative; display:block; height:10px; margin-top:6px; background:var(--fill); border-radius:5px; }
  .bar b { display:block; height:100%; border-radius:5px; background:var(--bar); } .bar b.over { background:var(--over); }
  .bar .budget { position:absolute; top:-3px; width:2px; height:16px; background:var(--fg); }
  .tag { font-size:12.5px; font-weight:600; white-space:nowrap; } .ok { color:var(--ok); } .over, .overtext { color:var(--over); } .fail { color:var(--warn); }
  li { margin:6px 0; }
</style></head>
<body><main>
<header><h1>Performance: ${esc(run.label)}</h1>
<p>${esc(run.target)} · commit ${esc(run.commit || '?')}${run.dirty ? ' with uncommitted changes' : ''} · ${esc(run.startedAt.slice(0, 16).replace('T', ' '))} UTC. Open a row for the detail behind it; the tick on each bar is the budget.</p></header>
${sections.join('\n')}
</main></body></html>`
}
