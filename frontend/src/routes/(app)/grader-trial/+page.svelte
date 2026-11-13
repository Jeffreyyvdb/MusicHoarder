<script lang="ts">
  import PageToolbarV2 from '#lib/components/v2/PageToolbarV2.svelte';
  import { onMount } from 'svelte';
  import { Download, FlaskConical, Loader2, Play, RefreshCw, Repeat, TriangleAlert } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import * as DropdownMenu from '#lib/components/ui/dropdown-menu/index.ts';
  import * as GroupedList from '#lib/components/ui/grouped-list/index.ts';
  import { Button } from '#lib/components/ui/button/index.ts';
  import { Skeleton } from '#lib/components/ui/skeleton/index.ts';
  import { ScrollArea } from '#lib/components/ui/scroll-area/index.ts';
  import {
    fetchGraderTrialQueue,
    fetchGraderTrialReport,
    fetchGraderTrialStatus,
    graderTrialCsvUrl,
    runGraderTrial,
    runGraderTrialStability,
    saveGraderTrialLabel,
    type GraderTrialAnswer,
    type GraderTrialKind,
    type GraderTrialLabelValue,
    type GraderTrialQueueItem,
    type GraderTrialReport,
    type GraderTrialStatus
  } from '#lib/api-client.ts';
  import { verdictBadge, verdictGlyph } from '#lib/quality-ui.ts';
  import { checkLabel, checkTone, checkWord, ms, num, pct, ratio, recommendationText, shortModel, usd } from '#lib/grader-trial.ts';
  import { cn } from '#lib/utils.ts';

  // The grader trial: the chat grader and the decision models (Jev) grade the same dossiers, the
  // decision models in shadow. This page shows what the trial has measured and asks for the one
  // thing the server cannot measure itself: which grader was right. Nothing here changes a grade.

  let kind = $state<GraderTrialKind>('song');
  let status = $state<GraderTrialStatus | null>(null);
  let report = $state<GraderTrialReport | null>(null);
  let queue = $state<GraderTrialQueueItem[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let busy = $state<string | null>(null);
  /** Per queue item: the "no source supports this identity" box, ticked before pressing Wrong. */
  let unsupported = $state<Record<string, boolean>>({});
  let saving = $state<string | null>(null);

  async function load() {
    // A tab switch mid-load must not let the slower, older kind land on the newer tab.
    const forKind = kind;
    loading = true;
    try {
      const [s, r, q] = await Promise.all([fetchGraderTrialStatus(), fetchGraderTrialReport(forKind), fetchGraderTrialQueue(forKind)]);
      if (forKind !== kind) return;
      status = s;
      report = r;
      queue = q;
      error = null;
    } catch (e) {
      if (forKind === kind) error = e instanceof Error ? e.message : 'Failed to load the grader trial';
    } finally {
      if (forKind === kind) loading = false;
    }
  }

  onMount(() => {
    void load();
  });

  function selectKind(next: string) {
    if (next !== 'song' && next !== 'album') return;
    kind = next;
    unsupported = {};
    void load();
  }

  async function act(label: string, run: () => Promise<{ graded: number; failed: number; stale: number }>) {
    if (busy) return;
    busy = label;
    try {
      const r = await run();
      toast.success(`${r.graded} graded`, {
        description: [r.failed ? `${r.failed} failed` : null, r.stale ? `${r.stale} changed since their grade` : null]
          .filter(Boolean)
          .join(' · ') || undefined
      });
      await load();
    } catch (e) {
      toast.error("Couldn't run the trial", { description: e instanceof Error ? e.message : undefined });
    } finally {
      busy = null;
    }
  }

  const key = (item: GraderTrialQueueItem) => `${item.subjectId}:${item.fingerprint}`;

  async function label(item: GraderTrialQueueItem, value: GraderTrialLabelValue) {
    const k = key(item);
    saving = k;
    try {
      await saveGraderTrialLabel(item, kind, value, value === 'wrong' && !!unsupported[k]);
      queue = queue.filter((q) => key(q) !== k);
      if (queue.length < 5) {
        // Keep a few ready, and pick up the new label in the report's numbers.
        const forKind = kind;
        const [r, q] = await Promise.all([fetchGraderTrialReport(forKind), fetchGraderTrialQueue(forKind)]);
        if (forKind !== kind) return;
        report = r;
        queue = q;
      }
    } catch (e) {
      toast.error("Couldn't save the label", { description: e instanceof Error ? e.message : undefined });
    } finally {
      saving = null;
    }
  }

  function subjectHref(item: GraderTrialQueueItem): string {
    if (kind === 'song') return `/track/${item.subjectId}`;
    const albumKey = `${(item.artist ?? '').toLowerCase()}::${(item.album ?? '').toLowerCase()}`;
    return `/library?album=${encodeURIComponent(albumKey)}`;
  }

  /** The chat grader's arm: the incumbent model with the most answers (normally the only one). */
  const incumbent = $derived(
    report?.arms.filter((a) => a.isIncumbent).sort((a, b) => b.answers - a.answers)[0]
  );
  const meta = $derived(
    status && !status.configured
      ? 'Off'
      : report
        ? `${report.comparisons.reduce((n, c) => Math.max(n, c.pairs), 0).toLocaleString()} pairs · ${report.labels.labelled} labelled`
        : undefined
  );
</script>

{#snippet answer(a: GraderTrialAnswer, title: string)}
  <div class="flex min-w-0 flex-1 flex-col gap-1">
    <span class="text-footnote text-muted-foreground md:text-xs">{title}</span>
    <span class="flex flex-wrap items-center gap-1.5">
      <span
        class={cn(
          'text-caption-1 inline-flex h-6 items-center gap-1 rounded-full border px-2 font-semibold',
          verdictBadge(a.verdict ?? undefined)
        )}
      >
        <span aria-hidden="true">{verdictGlyph(a.verdict ?? undefined)}</span>
        {a.verdict ?? '—'}
        {#if a.score != null}<span class="tabular-nums">{a.score}</span>{/if}
      </span>
      {#if a.flagProbability != null}
        <span class="text-footnote text-muted-foreground tabular-nums md:text-xs">flag {pct(a.flagProbability)}</span>
      {/if}
    </span>
    {#if a.summary}
      <span class="text-subheadline text-muted-foreground line-clamp-3 md:text-xs">{a.summary}</span>
    {/if}
    {#if a.issues.length > 0}
      <span class="text-footnote text-muted-foreground md:text-xs">{a.issues.join(' · ')}</span>
    {/if}
  </div>
{/snippet}

<div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
  <ScrollArea class="min-h-0 flex-1" viewportClass="overscroll-contain">
    <PageToolbarV2
      title="Grader trial"
      {meta}
      grouped
      tabs={[
        { id: 'song', label: 'Songs' },
        { id: 'album', label: 'Albums' }
      ]}
      activeTab={kind}
      onselectTab={selectKind}
    >
      {#snippet actions()}
        <Button
          onclick={() => act('run', () => runGraderTrial(kind, 50))}
          disabled={!!busy || !status?.configured}
          class="rounded-full"
        >
          {#if busy === 'run'}
            <Loader2 class="animate-spin" aria-hidden="true" />
          {:else}
            <Play aria-hidden="true" />
          {/if}
          <span class="max-md:sr-only">Run 50 now</span>
        </Button>
      {/snippet}
      {#snippet more()}
        <DropdownMenu.Item
          onSelect={() => act('stability', () => runGraderTrialStability(kind, 100, false))}
          disabled={!!busy || !status?.configured}
        >
          <Repeat />
          Stability check (decision models)
        </DropdownMenu.Item>
        <DropdownMenu.Item
          onSelect={() => act('stability', () => runGraderTrialStability(kind, 50, true))}
          disabled={!!busy || !status?.configured}
        >
          <Repeat />
          Stability check, chat grader too (uses credits)
        </DropdownMenu.Item>
        <DropdownMenu.Item onSelect={() => location.assign(graderTrialCsvUrl(kind))}>
          <Download />
          Export CSV
        </DropdownMenu.Item>
        <DropdownMenu.Item onSelect={load}>
          <RefreshCw />
          Refresh
        </DropdownMenu.Item>
      {/snippet}
    </PageToolbarV2>

    <div class="mx-auto flex w-full max-w-4xl flex-col gap-7 pt-2 pb-(--mh-content-pad) md:gap-6 md:px-7 md:pt-6">
      {#if status && !status.configured}
        <GroupedList.Section>
          <GroupedList.Row
            icon={TriangleAlert}
            iconClass="bg-warning/15 text-warning-text"
            label="The trial is off"
            sublabel="It needs JevGrading:Enabled and the QualityGrading OpenRouter key. Answers recorded so far are still shown."
          />
        </GroupedList.Section>
      {/if}

      {#if error}
        <GroupedList.Section>
          <GroupedList.Row
            icon={TriangleAlert}
            iconClass="bg-destructive/12 text-destructive-text"
            label="Couldn't load the trial"
            sublabel={error}
          />
        </GroupedList.Section>
      {:else if loading && !report}
        <GroupedList.Section>
          {#each Array(5) as _, i (i)}
            <div class="flex items-center justify-between px-4 py-3">
              <Skeleton class="h-4 w-32" />
              <Skeleton class="h-4 w-12" />
            </div>
          {/each}
        </GroupedList.Section>
      {:else if report}
        {#if report.comparisons.length === 0}
          <div class="flex flex-col items-center gap-2 px-6 py-12 text-center">
            <FlaskConical class="text-muted-foreground size-8" aria-hidden="true" />
            <p class="text-body text-muted-foreground max-w-md md:text-sm">
              No decision-model answers yet. The shadow sweep pairs with grades as they are written; “Run 50 now” starts
              with the newest grades.
            </p>
          </div>
        {/if}

        {#each report.comparisons as c (c.model)}
          {@const rec = recommendationText(c.recommendation)}
          {@const arm = report.arms.find((a) => !a.isIncumbent && a.model === c.model)}
          <GroupedList.Section
            headingLevel={2}
            header={`${shortModel(c.model)} against ${incumbent ? shortModel(incumbent.model) : 'the chat grader'}`}
            footer="The checks were fixed before any result came in. “Too few” means more answers or labels are needed."
          >
            <GroupedList.Row label={rec.title} sublabel={rec.detail} />
            {#each c.checks as check (check.name)}
              <GroupedList.Row label={checkLabel(check.name)} sublabel={check.detail}>
                {#snippet trailing()}
                  <span class={cn('text-caption-1 rounded-full px-2 py-0.5 font-semibold', checkTone(check.status))}>
                    {checkWord(check.status)}
                  </span>
                {/snippet}
              </GroupedList.Row>
            {/each}
          </GroupedList.Section>

          <GroupedList.Section headingLevel={2} header="Speed and price" footer="Per grade, retries included. Costs are what OpenRouter billed (usage.cost).">
            <div class="overflow-x-auto" data-scroll-x>
              <table class="w-full min-w-[520px] text-sm tabular-nums">
                <thead>
                  <tr class="text-muted-foreground text-left text-xs">
                    <th class="px-4 py-2 font-medium">Grader</th>
                    <th class="px-3 py-2 font-medium">p50</th>
                    <th class="px-3 py-2 font-medium">p95</th>
                    <th class="px-3 py-2 font-medium">Per 1,000</th>
                    <th class="px-3 py-2 font-medium">No grade</th>
                    <th class="px-3 py-2 font-medium">Attempts</th>
                    <th class="px-3 py-2 font-medium">Flips</th>
                  </tr>
                </thead>
                <tbody class="divide-separator divide-y">
                  {#each [incumbent, arm].filter((a) => a != null) as a (a.arm)}
                    <tr>
                      <td class="px-4 py-2.5">
                        <span class="font-medium">{shortModel(a.model)}</span>
                        <span class="text-muted-foreground block text-xs">{a.answers.toLocaleString()} answers</span>
                      </td>
                      <td class="px-3 py-2.5">{ms(a.latencyP50Ms)}</td>
                      <td class="px-3 py-2.5">{ms(a.latencyP95Ms)}</td>
                      <td class="px-3 py-2.5">
                        {usd(a.costPer1000Usd)}
                        {#if a.costKnown < a.answers}<span class="text-muted-foreground block text-xs">{a.costKnown} priced</span>{/if}
                      </td>
                      <td class="px-3 py-2.5">{pct(a.noGradeRate)}</td>
                      <td class="px-3 py-2.5">{num(a.averageAttempts)}</td>
                      <td class="px-3 py-2.5">
                        {a.stability.flipRate == null ? '—' : pct(a.stability.flipRate)}
                        {#if a.stability.pairs > 0}<span class="text-muted-foreground block text-xs">of {a.stability.pairs}</span>{/if}
                      </td>
                    </tr>
                  {/each}
                </tbody>
              </table>
            </div>
            {#if incumbent && arm}
              {@const faster = ratio(incumbent.latencyP95Ms, arm.latencyP95Ms)}
              {@const cheaper = ratio(incumbent.costPer1000Usd, arm.costPer1000Usd)}
              {#if faster || cheaper}
                <GroupedList.Row
                  label={[faster ? `${faster} faster at p95` : null, cheaper ? `${cheaper} cheaper` : null].filter(Boolean).join(' · ')}
                />
              {/if}
            {/if}
            {#if arm?.coverage}
              <GroupedList.Row
                label="Dossiers that fit"
                sublabel={`${pct(arm.coverage.fitWithoutShedding)} whole, ${pct(arm.coverage.fitAfterShedding)} after shedding the change log · limit ${arm.coverage.maxStateBytes.toLocaleString()} bytes`}
              />
            {/if}
          </GroupedList.Section>

          <GroupedList.Section headingLevel={2} header="Agreement" footer="Rows: the chat grader's verdict. Columns: the decision model's.">
            <GroupedList.Row label="Same flag decision" value={pct(c.flagAgreement)} />
            <GroupedList.Row label="Cohen's kappa (verdict)" value={num(c.kappa)} />
            <GroupedList.Row label="Mean score difference" value={c.scoreMae == null ? '—' : `${num(c.scoreMae, 1)} points`} />
            <div class="overflow-x-auto px-4 py-3" data-scroll-x>
              <table class="min-w-[420px] text-xs tabular-nums">
                <thead>
                  <tr class="text-muted-foreground">
                    <th class="py-1 pr-3 text-left font-medium"><span class="sr-only">Chat grader verdict</span></th>
                    {#each report.verdictOrder as v (v)}
                      <th class="px-2 py-1 text-right font-medium">{v}</th>
                    {/each}
                  </tr>
                </thead>
                <tbody>
                  {#each c.confusion as row, i (i)}
                    <tr>
                      <th class="text-muted-foreground py-1 pr-3 text-left font-medium">{report.verdictOrder[i]}</th>
                      {#each row as n, j (j)}
                        <td class={cn('px-2 py-1 text-right', i === j ? 'font-semibold' : n > 0 ? '' : 'text-muted-foreground-dim')}>
                          {n}
                        </td>
                      {/each}
                    </tr>
                  {/each}
                </tbody>
              </table>
            </div>
          </GroupedList.Section>

          <GroupedList.Section
            headingLevel={2}
            header="Against your labels"
            footer="Disagreements are oversampled, so each label counts by its group's share of all pairs."
          >
            <GroupedList.Row label="Precision" sublabel="Of the flagged, how many were wrong">
              {#snippet trailing()}
                <span class="text-muted-foreground tabular-nums">{pct(c.challenger.precision)} vs {pct(c.incumbent.precision)}</span>
              {/snippet}
            </GroupedList.Row>
            <GroupedList.Row label="Recall" sublabel="Of the wrong, how many were flagged">
              {#snippet trailing()}
                <span class="text-muted-foreground tabular-nums">{pct(c.challenger.recall)} vs {pct(c.incumbent.recall)}</span>
              {/snippet}
            </GroupedList.Row>
            {#if kind === 'song'}
              <GroupedList.Row label="Unsupported identity caught" sublabel={`${c.challenger.unsupportedLabelled} labelled`}>
                {#snippet trailing()}
                  <span class="text-muted-foreground tabular-nums">
                    {pct(c.challenger.unsupportedRecall)} vs {pct(c.incumbent.unsupportedRecall)}
                  </span>
                {/snippet}
              </GroupedList.Row>
            {/if}
            <GroupedList.Row label="Brier score" sublabel="Of the flag probability; lower is better" value={num(c.challenger.brier, 3)} />
            <GroupedList.Row
              label="Labelled"
              value={`${c.challenger.labelled} (${c.challenger.labelledWrong} wrong)`}
            />
          </GroupedList.Section>
        {/each}

        <GroupedList.Section
          headingLevel={2}
          header="Label"
          footer={`Is this ${kind}'s metadata right? ${report.labels.disagreementsLabelled} of ${report.labels.disagreements} disagreements and ${report.labels.agreementsLabelled} of ${report.labels.agreements} agreements labelled. About 200 labels is enough.`}
        >
          {#if queue.length === 0}
            <p class="text-body text-muted-foreground px-4 py-8 text-center md:text-sm">Nothing to label right now.</p>
          {:else}
            {#each queue as item (key(item))}
              {@const k = key(item)}
              <div class="flex flex-col gap-3 px-4 py-3.5">
                <div class="flex min-w-0 items-baseline justify-between gap-3">
                  <a href={subjectHref(item)} class="min-w-0 hover:underline">
                    <span class="text-body block truncate font-medium md:text-sm">
                      {kind === 'song' ? (item.title ?? item.fileName ?? `Song ${item.subjectId}`) : (item.album ?? `Album ${item.subjectId}`)}
                    </span>
                    <span class="text-subheadline text-muted-foreground block truncate md:text-xs">
                      {item.artist ?? '—'}{kind === 'song' && item.album ? ` · ${item.album}` : ''}
                    </span>
                  </a>
                  <span class="text-caption-1 text-muted-foreground shrink-0">
                    {item.stratum === 'Disagreement' ? 'Disagree' : 'Agree'}
                  </span>
                </div>
                <div class="flex flex-col gap-3 sm:flex-row">
                  {@render answer(item.incumbent, `Chat · ${shortModel(item.incumbent.model)}`)}
                  {@render answer(item.challenger, shortModel(item.challenger.model))}
                </div>
                <div class="flex flex-wrap items-center gap-2">
                  <Button size="sm" variant="secondary" disabled={saving === k} onclick={() => label(item, 'right')}>Right</Button>
                  <Button size="sm" variant="secondary" disabled={saving === k} onclick={() => label(item, 'wrong')}>Wrong</Button>
                  <Button size="sm" variant="ghost" disabled={saving === k} onclick={() => label(item, 'unsure')}>Unsure</Button>
                  {#if kind === 'song'}
                    <label class="text-subheadline text-muted-foreground ml-1 inline-flex items-center gap-2 md:text-xs">
                      <input
                        id={`unsupported-${k}`}
                        type="checkbox"
                        class="accent-primary size-4"
                        bind:checked={unsupported[k]}
                      />
                      No source supports this identity
                    </label>
                  {/if}
                </div>
              </div>
            {/each}
          {/if}
        </GroupedList.Section>
      {/if}
    </div>
  </ScrollArea>
</div>
