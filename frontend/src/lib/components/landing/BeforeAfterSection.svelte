<script lang="ts">
  import { Folder, FileAudio, HelpCircle, Check, ArrowRight } from '@lucide/svelte';
</script>

<section class="border-border border-t px-6 py-24">
  <div class="mx-auto max-w-6xl">
    <div class="mb-16 text-center">
      <h2 class="mb-4 text-3xl font-bold md:text-4xl">See the transformation</h2>
      <p class="text-muted-foreground mx-auto max-w-xl text-lg">
        From chaos to a perfectly organized library with correct metadata, album art, and folder
        structure.
      </p>
    </div>

    <div class="grid gap-8 md:grid-cols-2 lg:gap-12">
      <div class="relative">
        <div
          class="bg-destructive/10 text-destructive border-destructive/20 absolute -top-4 left-4 rounded-full border px-3 py-1 text-sm font-medium"
        >
          Before
        </div>
        <div class="bg-card border-border rounded-xl border p-6 font-mono text-sm">
          <div class="space-y-3">
            <div class="text-muted-foreground flex items-center gap-3">
              <Folder class="text-muted-foreground/50 h-4 w-4" />
              <span>Unknown Artist/</span>
            </div>
            <div class="space-y-2 pl-6">
              <div class="text-muted-foreground/70 flex items-center gap-3">
                <FileAudio class="h-4 w-4" /><span>track01.mp3</span>
              </div>
              <div class="text-muted-foreground/70 flex items-center gap-3">
                <FileAudio class="h-4 w-4" /><span>taylor swift leak 2024.mp3</span>
              </div>
              <div class="text-muted-foreground/70 flex items-center gap-3">
                <FileAudio class="h-4 w-4" /><span>untitled (2).flac</span>
              </div>
              <div class="text-muted-foreground/70 flex items-center gap-3">
                <HelpCircle class="h-4 w-4" /><span>?????.mp3</span>
              </div>
            </div>
            <div class="text-muted-foreground mt-4 flex items-center gap-3">
              <Folder class="text-muted-foreground/50 h-4 w-4" />
              <span>Downloads/</span>
            </div>
            <div class="space-y-2 pl-6">
              <div class="text-muted-foreground/70 flex items-center gap-3">
                <FileAudio class="h-4 w-4" /><span>billie eilish birds FINAL.mp3</span>
              </div>
              <div class="text-muted-foreground/70 flex items-center gap-3">
                <FileAudio class="h-4 w-4" /><span>weeknd - blinding lights (1).mp3</span>
              </div>
            </div>
          </div>
        </div>
      </div>

      <div class="flex items-center justify-center md:hidden">
        <ArrowRight class="text-primary h-8 w-8 rotate-90" />
      </div>

      <div class="relative">
        <div
          class="bg-primary/10 text-primary border-primary/20 absolute -top-4 left-4 rounded-full border px-3 py-1 text-sm font-medium"
        >
          After
        </div>
        <div class="bg-card border-primary/20 rounded-xl border p-6 font-mono text-sm">
          <div class="space-y-3">
            <div class="text-foreground flex items-center gap-3">
              <Folder class="text-primary h-4 w-4" />
              <span>Taylor Swift/</span>
            </div>
            <div class="space-y-1 pl-6">
              <div class="text-muted-foreground flex items-center gap-3">
                <Folder class="text-primary/70 h-4 w-4" />
                <span>1989 (Taylor's Version)/</span>
              </div>
              <div class="space-y-2 pl-6">
                <div class="text-foreground/80 flex items-center gap-3">
                  <Check class="text-primary h-4 w-4" /><span>01 - Welcome to New York.flac</span>
                </div>
                <div class="text-foreground/80 flex items-center gap-3">
                  <Check class="text-primary h-4 w-4" /><span>02 - Blank Space.flac</span>
                </div>
              </div>
            </div>
            <div class="text-foreground mt-4 flex items-center gap-3">
              <Folder class="text-primary h-4 w-4" />
              <span>Billie Eilish/</span>
            </div>
            <div class="space-y-1 pl-6">
              <div class="text-muted-foreground flex items-center gap-3">
                <Folder class="text-primary/70 h-4 w-4" />
                <span>When We All Fall Asleep.../</span>
              </div>
              <div class="pl-6">
                <div class="text-foreground/80 flex items-center gap-3">
                  <Check class="text-primary h-4 w-4" /><span>01 - bad guy.flac</span>
                </div>
              </div>
            </div>
            <div class="text-foreground mt-4 flex items-center gap-3">
              <Folder class="text-primary h-4 w-4" />
              <span>The Weeknd/</span>
            </div>
            <div class="space-y-1 pl-6">
              <div class="text-muted-foreground flex items-center gap-3">
                <Folder class="text-primary/70 h-4 w-4" />
                <span>After Hours/</span>
              </div>
              <div class="pl-6">
                <div class="text-foreground/80 flex items-center gap-3">
                  <Check class="text-primary h-4 w-4" /><span>09 - Blinding Lights.flac</span>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</section>
