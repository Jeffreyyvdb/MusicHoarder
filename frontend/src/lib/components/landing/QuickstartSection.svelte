<script lang="ts">
  import CommandBlock from '$lib/components/landing/CommandBlock.svelte';
  import { composeSnippet, githubUrl, quickstartSteps } from '$lib/components/landing/landing-demo-data';
</script>

<section id="quickstart" class="mx-auto max-w-[1280px] scroll-mt-8 px-6 py-14 md:px-14">
  <div class="text-muted-foreground font-mono text-[11px] font-semibold tracking-[0.12em] uppercase">
    QUICKSTART · ~2 MINUTES
  </div>
  <h2 class="mt-2 mb-3 text-[clamp(26px,3vw,34px)] font-bold tracking-[-0.025em] text-balance">
    Self-host it in three steps.
  </h2>
  <p class="text-muted-foreground max-w-[640px] text-[14.5px] leading-[1.6] text-pretty">
    No account, no SaaS. <strong class="text-foreground">Grab two files</strong>, point it at two folders,
    open a browser. An <strong class="text-foreground">AcoustID key</strong> is optional but sharpens matching.
  </p>

  <div class="mt-10 grid grid-cols-1 items-start gap-10 lg:grid-cols-[1fr_1.1fr]">
    <ol class="flex flex-col gap-7">
      {#each quickstartSteps as step, i (step.title)}
        <li class="flex gap-4">
          <span
            class="bg-primary/10 text-primary grid size-9 shrink-0 place-items-center rounded-lg font-mono font-semibold"
          >
            {i + 1}
          </span>
          <div class="min-w-0">
            <div class="text-[15px] font-semibold">{step.title}</div>
            <p class="text-muted-foreground mt-1 text-[13.5px] leading-[1.55]">{step.body}</p>
          </div>
        </li>
      {/each}
    </ol>

    <div>
      <CommandBlock text={composeSnippet} label="docker-compose.yml" />
      <p class="text-muted-foreground mt-3 text-[13px] leading-[1.55]">
        Abridged — the full compose file (with Postgres, env, and healthchecks) lives in the repo.
        <a
          href={githubUrl}
          target="_blank"
          rel="noopener noreferrer"
          class="text-primary font-medium hover:underline"
        >
          docker-compose.yml →
        </a>
      </p>
    </div>
  </div>
</section>
