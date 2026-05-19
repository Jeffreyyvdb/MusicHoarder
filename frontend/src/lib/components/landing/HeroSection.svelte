<script lang="ts">
  import { Button } from '$lib/components/ui/button';
  import ThemeToggle from '$lib/components/ThemeToggle.svelte';
  import GithubIcon from '$lib/components/GithubIcon.svelte';
  import { Star } from '@lucide/svelte';
</script>

<section
  class="relative flex min-h-screen flex-col items-center justify-center overflow-hidden px-6 py-24"
>
  <div class="absolute top-4 right-4 z-20 sm:top-6 sm:right-6 md:top-8 md:right-8">
    <ThemeToggle />
  </div>

  <div
    class="absolute inset-0 bg-[linear-gradient(oklch(0%_0_0/0.06)_1px,transparent_1px),linear-gradient(90deg,oklch(0%_0_0/0.06)_1px,transparent_1px)] bg-[size:64px_64px] dark:bg-[linear-gradient(rgba(255,255,255,0.03)_1px,transparent_1px),linear-gradient(90deg,rgba(255,255,255,0.03)_1px,transparent_1px)]"
  ></div>

  <div class="bg-primary/10 absolute top-1/4 left-1/4 h-96 w-96 rounded-full blur-3xl"></div>
  <div class="bg-accent/5 absolute right-1/4 bottom-1/4 h-96 w-96 rounded-full blur-3xl"></div>

  <div class="relative z-10 mx-auto max-w-4xl text-center">
    <div
      class="border-border bg-card/50 mb-8 inline-flex items-center gap-2 rounded-full border px-4 py-2 text-sm backdrop-blur-sm"
    >
      <span class="bg-primary h-2 w-2 animate-pulse rounded-full"></span>
      <span class="text-muted-foreground">Self-hosted & Open Source</span>
    </div>

    <h1 class="mb-6 text-4xl font-bold tracking-tight text-balance md:text-6xl lg:text-7xl">
      Your music library is a mess.
      <span class="text-primary mt-2 block">MusicHoarder fixes it.</span>
    </h1>

    <p
      class="text-muted-foreground mx-auto mb-12 max-w-2xl text-lg leading-relaxed text-pretty md:text-xl"
    >
      Wrong titles, missing albums, files named "track01_final_FINAL.mp3". MusicHoarder identifies,
      enriches, and organizes your entire collection automatically.
    </p>

    <div class="flex flex-col items-center justify-center gap-4 sm:flex-row">
      <Button
        size="lg"
        class="h-12 gap-2 px-8 text-base font-medium"
        href="https://github.com/Jeffreyyvdb/MusicHoarder"
        target="_blank"
        rel="noopener noreferrer"
      >
        <Star class="h-5 w-5" />
        Star on GitHub
      </Button>
      <Button
        size="lg"
        variant="outline"
        class="h-12 gap-2 px-8 text-base font-medium"
        href="/app"
      >
        Launch App
      </Button>
    </div>

    <div class="text-muted-foreground mt-12 flex items-center justify-center gap-8 text-sm">
      <div class="flex items-center gap-2">
        <GithubIcon class="h-4 w-4" />
        <span>MIT License</span>
      </div>
      <div class="flex items-center gap-2">
        <span class="bg-primary h-1.5 w-1.5 rounded-full"></span>
        <span>.NET + SvelteKit</span>
      </div>
      <div class="flex items-center gap-2">
        <span class="bg-primary h-1.5 w-1.5 rounded-full"></span>
        <span>Docker Ready</span>
      </div>
    </div>
  </div>
</section>
