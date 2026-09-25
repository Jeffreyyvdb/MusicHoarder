<script lang="ts">
  import { ChevronRight } from '@lucide/svelte';
  import CommandBlock from '$lib/components/landing/CommandBlock.svelte';
  import LandingSection from '$lib/components/landing/LandingSection.svelte';
  import {
    composeSnippet,
    githubUrl,
    quickstartSteps
  } from '$lib/components/landing/landing-content';
</script>

<LandingSection
  id="quickstart"
  eyebrow="Self-host"
  title="Up and running in three steps."
  lead="No account and no subscription. Grab two files, point them at two folders and open a browser. An AcoustID key is optional, but sharpens matching."
>
  <div class="mt-12 grid grid-cols-1 items-start gap-10 md:mt-16 lg:grid-cols-[1fr_1.1fr]">
    <ol class="flex flex-col gap-8">
      {#each quickstartSteps as step, i (step.title)}
        <li class="flex gap-4">
          <span
            class="bg-muted text-foreground grid size-11 shrink-0 place-items-center rounded-full text-[17px] font-semibold"
            aria-hidden="true"
          >
            {i + 1}
          </span>
          <div class="min-w-0 pt-2">
            <h3 class="text-[19px] leading-[24px] font-semibold">{step.title}</h3>
            <p class="text-muted-foreground mt-2 text-[15px] leading-[21px] text-pretty">
              {step.body}
            </p>
          </div>
        </li>
      {/each}
    </ol>

    <div class="min-w-0">
      <CommandBlock text={composeSnippet} label="docker-compose.yml" />
      <p class="text-muted-foreground mt-4 text-[15px] leading-[21px]">
        Abridged — the full compose file, with Postgres, the environment and health checks, lives
        in the repository.
        <a
          href={githubUrl}
          target="_blank"
          rel="noopener noreferrer"
          class="text-primary inline-flex items-center font-medium whitespace-nowrap hover:underline pointer-coarse:min-h-11"
        >
          docker-compose.yml
          <ChevronRight class="size-4" aria-hidden="true" />
        </a>
      </p>
    </div>
  </div>
</LandingSection>
