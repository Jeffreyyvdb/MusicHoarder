<script lang="ts">
  import { Heart } from '@lucide/svelte';
  import GithubIcon from '$lib/components/GithubIcon.svelte';
</script>

<footer class="border-border border-t px-6 py-8">
  <div
    class="mx-auto flex max-w-6xl flex-col items-center justify-between gap-4 md:flex-row"
  >
    <div class="text-muted-foreground flex items-center gap-2 text-sm">
      <span>Built with</span>
      <Heart class="text-primary fill-primary h-4 w-4" />
      <span>by</span>
      <a
        href="https://github.com/Jeffreyyvdb"
        target="_blank"
        rel="noopener noreferrer"
        class="text-foreground hover:text-primary transition-colors"
      >
        @Jeffreyyvdb
      </a>
    </div>

    <div class="text-muted-foreground flex items-center gap-6 text-sm">
      <a
        href="https://github.com/Jeffreyyvdb/MusicHoarder"
        target="_blank"
        rel="noopener noreferrer"
        class="hover:text-foreground flex items-center gap-2 transition-colors"
      >
        <GithubIcon class="h-4 w-4" />
        <span>GitHub</span>
      </a>
      <a
        href="https://x.com/jeffreyyvdb"
        target="_blank"
        rel="noopener noreferrer"
        class="hover:text-foreground flex items-center gap-2 transition-colors"
        aria-label="Follow @jeffreyyvdb on X"
      >
        <svg viewBox="0 0 24 24" aria-hidden="true" class="h-4 w-4 fill-current">
          <path
            d="M18.244 2.25h3.308l-7.227 8.26 8.502 11.24H16.17l-5.214-6.817L4.99 21.75H1.68l7.73-8.835L1.254 2.25H8.08l4.713 6.231zm-1.161 17.52h1.833L7.084 4.126H5.117z"
          />
        </svg>
        <span>@jeffreyyvdb</span>
      </a>
      <span>MIT License</span>
    </div>
  </div>
</footer>
