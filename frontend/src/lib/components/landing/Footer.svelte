<script lang="ts">
  import { page } from '$app/state';

  const version = $derived(page.data.appVersion as string | null | undefined);

  const links: ReadonlyArray<{ label: string; href: string }> = [
    { label: 'About', href: '/about' },
    { label: 'Contact', href: '/contact' },
    { label: 'Privacy', href: '/privacy' },
    { label: 'Docs', href: 'https://github.com/Jeffreyyvdb/MusicHoarder#readme' },
    { label: 'Changelog', href: 'https://github.com/Jeffreyyvdb/MusicHoarder/releases' },
    { label: 'GitHub', href: 'https://github.com/Jeffreyyvdb/MusicHoarder' }
  ];
</script>

<!-- On the grouped band, like the last section above it, with a hairline between. On touch every
     link grows a 44pt hit area around its 13px type with padding, not bigger text; the row is
     pulled out by as much so the words sit where they did. -->
<footer class="bg-background-grouped px-4 pb-[max(1.5rem,env(safe-area-inset-bottom))] md:px-10">
  <div
    class="border-separator text-muted-foreground mx-auto flex max-w-[1200px] flex-col items-start justify-between gap-3 border-t pt-5 text-[13px] leading-[18px] md:flex-row md:items-center"
  >
    <p>
      © 2026 MusicHoarder · MIT licensed · self-hosted{version ? ` · v${version}` : ''}
    </p>
    <nav
      aria-label="Footer"
      class="flex flex-wrap gap-x-5 gap-y-1 pointer-coarse:-mx-2 pointer-coarse:gap-x-0 pointer-coarse:gap-y-0"
    >
      {#each links as link (link.href)}
        {@const external = !link.href.startsWith('/')}
        <a
          href={link.href}
          target={external ? '_blank' : null}
          rel={external ? 'noopener noreferrer' : null}
          class="hover:text-foreground transition-colors pointer-coarse:inline-flex pointer-coarse:min-h-11 pointer-coarse:min-w-11 pointer-coarse:items-center pointer-coarse:justify-center pointer-coarse:px-2"
        >
          {link.label}
        </a>
      {/each}
    </nav>
  </div>
</footer>
