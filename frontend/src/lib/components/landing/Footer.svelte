<script lang="ts">
  import { page } from '$app/state';

  const version = $derived(page.data.appVersion as string | null | undefined);
</script>

<footer
  class="border-border text-muted-foreground mx-auto mt-12 flex max-w-[1280px] flex-col items-start justify-between gap-3 border-t px-6 py-6 font-mono text-[11px] md:flex-row md:items-center md:px-14"
>
  <div>
    © 2026 MusicHoarder · self-hosted{version ? ` release v${version}` : ''} · no music files were harmed
  </div>
  <!-- On touch the links grow a 44pt hit area around the 11px type (padding, not bigger text):
       44 tall, and 8px either side with a 44 floor, so "docs" is 44 wide too. The padding replaces
       the gap, and the row is pulled out by as much, so the words sit where they did. -->
  <div
    class="flex flex-wrap gap-x-4 gap-y-1 pointer-coarse:-mx-2 pointer-coarse:gap-x-0 pointer-coarse:gap-y-0 [&>a]:pointer-coarse:inline-flex [&>a]:pointer-coarse:min-h-11 [&>a]:pointer-coarse:min-w-11 [&>a]:pointer-coarse:items-center [&>a]:pointer-coarse:justify-center [&>a]:pointer-coarse:px-2"
  >
    <a href="/about" class="hover:text-foreground transition-colors">about</a>
    <a href="/contact" class="hover:text-foreground transition-colors">contact</a>
    <a href="/privacy" class="hover:text-foreground transition-colors">privacy</a>
    <a
      href="https://github.com/Jeffreyyvdb/MusicHoarder#readme"
      target="_blank"
      rel="noopener noreferrer"
      class="hover:text-foreground transition-colors">docs</a
    >
    <a
      href="https://github.com/Jeffreyyvdb/MusicHoarder/releases"
      target="_blank"
      rel="noopener noreferrer"
      class="hover:text-foreground transition-colors">changelog</a
    >
    <a
      href="https://github.com/Jeffreyyvdb/MusicHoarder"
      target="_blank"
      rel="noopener noreferrer"
      class="hover:text-foreground transition-colors">github</a
    >
  </div>
</footer>
