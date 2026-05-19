<script lang="ts">
  import { Button } from '$lib/components/ui/button';
  import GithubIcon from '$lib/components/GithubIcon.svelte';
  import { Star } from '@lucide/svelte';
</script>

<section class="border-border border-t px-6 py-24">
  <div class="mx-auto max-w-3xl text-center">
    <h2 class="mb-4 text-3xl font-bold md:text-4xl">Ready to organize your library?</h2>
    <p class="text-muted-foreground mx-auto mb-8 max-w-xl text-lg">
      Stop fighting with messy filenames. Let MusicHoarder do the heavy lifting.
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
        href="https://github.com/Jeffreyyvdb/MusicHoarder"
        target="_blank"
        rel="noopener noreferrer"
      >
        <GithubIcon class="h-5 w-5" />
        View Documentation
      </Button>
    </div>
  </div>
</section>
