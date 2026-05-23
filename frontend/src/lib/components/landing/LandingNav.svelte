<script lang="ts">
  import { Button } from '$lib/components/ui/button';
  import ThemeToggle from '$lib/components/ThemeToggle.svelte';
</script>

<nav class="mx-auto flex max-w-[1280px] items-center justify-between px-6 py-6 md:px-14">
  <a href="/" class="flex items-center gap-2.5 text-[15px] font-semibold tracking-tight">
    <span
      class="bg-card grid h-7 w-7 place-items-center rounded-[7px]"
      style="box-shadow: 0 1px 2px rgba(0,0,0,0.04), 0 0 0 0.5px rgba(0,0,0,0.06);"
    >
      <svg width="18" height="18" viewBox="0 0 18 18" fill="none">
        <rect x="1" y="1" width="16" height="16" rx="3.5" class="fill-primary" />
        <path d="M5.5 12.5V6l6-1.2V11" stroke="#fff" stroke-width="1.4" stroke-linecap="round" />
        <circle cx="5" cy="12.5" r="1.3" fill="#fff" />
        <circle cx="11" cy="11" r="1.3" fill="#fff" />
      </svg>
    </span>
    <span>MusicHoarder</span>
    <span
      class="bg-surface-sunken text-muted-foreground ml-1 rounded-[3px] px-1.5 py-0.5 font-mono text-[10.5px] font-normal"
    >
      v0.4.2
    </span>
  </a>

  <div class="flex items-center gap-4 md:gap-5">
    <a
      href="#features"
      class="text-muted-foreground hover:text-foreground hidden text-[13px] transition-colors sm:inline"
    >
      Features
    </a>
    <a
      href="#pipeline"
      class="text-muted-foreground hover:text-foreground hidden text-[13px] transition-colors sm:inline"
    >
      Pipeline
    </a>
    <a
      href="#sources"
      class="text-muted-foreground hover:text-foreground hidden text-[13px] transition-colors sm:inline"
    >
      Sources
    </a>
    <a
      href="https://github.com/Jeffreyyvdb/MusicHoarder"
      target="_blank"
      rel="noopener noreferrer"
      class="text-muted-foreground hover:text-foreground hidden text-[13px] transition-colors sm:inline"
    >
      Source
    </a>
    <ThemeToggle />
    <Button variant="ghost" size="sm" href="/library">Sign in</Button>
  </div>
</nav>
