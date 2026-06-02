<script lang="ts">
  import { page } from '$app/state';

  const version = $derived(page.data.appVersion as string | null | undefined);
</script>

<footer
  class="border-border text-muted-foreground mx-auto mt-12 flex max-w-[1280px] flex-col items-start justify-between gap-3 border-t px-6 py-6 font-mono text-[11px] md:flex-row md:items-center md:px-14"
>
  <div>
    © 2026 MusicHoarder · self-hosted{version ? ` release v${version}` : ''} · no music files were
    harmed
  </div>
  <div class="flex gap-4">
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
