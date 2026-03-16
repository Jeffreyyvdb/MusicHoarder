import { Folder, FileAudio, HelpCircle, Check, ArrowRight } from "lucide-react"

export function BeforeAfterSection() {
  return (
    <section className="py-24 px-6 border-t border-border">
      <div className="max-w-6xl mx-auto">
        <div className="text-center mb-16">
          <h2 className="text-3xl md:text-4xl font-bold mb-4">
            See the transformation
          </h2>
          <p className="text-muted-foreground text-lg max-w-xl mx-auto">
            From chaos to a perfectly organized library with correct metadata, album art, and folder structure.
          </p>
        </div>
        
        <div className="grid md:grid-cols-2 gap-8 lg:gap-12">
          {/* Before */}
          <div className="relative">
            <div className="absolute -top-4 left-4 px-3 py-1 bg-destructive/10 text-destructive text-sm font-medium rounded-full border border-destructive/20">
              Before
            </div>
            <div className="bg-card border border-border rounded-xl p-6 font-mono text-sm">
              <div className="space-y-3">
                <div className="flex items-center gap-3 text-muted-foreground">
                  <Folder className="w-4 h-4 text-muted-foreground/50" />
                  <span>Unknown Artist/</span>
                </div>
                <div className="pl-6 space-y-2">
                  <div className="flex items-center gap-3 text-muted-foreground/70">
                    <FileAudio className="w-4 h-4" />
                    <span>track01.mp3</span>
                  </div>
                  <div className="flex items-center gap-3 text-muted-foreground/70">
                    <FileAudio className="w-4 h-4" />
                    <span>taylor swift leak 2024.mp3</span>
                  </div>
                  <div className="flex items-center gap-3 text-muted-foreground/70">
                    <FileAudio className="w-4 h-4" />
                    <span>untitled (2).flac</span>
                  </div>
                  <div className="flex items-center gap-3 text-muted-foreground/70">
                    <HelpCircle className="w-4 h-4" />
                    <span>?????.mp3</span>
                  </div>
                </div>
                <div className="flex items-center gap-3 text-muted-foreground mt-4">
                  <Folder className="w-4 h-4 text-muted-foreground/50" />
                  <span>Downloads/</span>
                </div>
                <div className="pl-6 space-y-2">
                  <div className="flex items-center gap-3 text-muted-foreground/70">
                    <FileAudio className="w-4 h-4" />
                    <span>billie eilish birds FINAL.mp3</span>
                  </div>
                  <div className="flex items-center gap-3 text-muted-foreground/70">
                    <FileAudio className="w-4 h-4" />
                    <span>weeknd - blinding lights (1).mp3</span>
                  </div>
                </div>
              </div>
            </div>
          </div>
          
          {/* Arrow for mobile */}
          <div className="flex md:hidden items-center justify-center">
            <ArrowRight className="w-8 h-8 text-primary rotate-90" />
          </div>
          
          {/* After */}
          <div className="relative">
            <div className="absolute -top-4 left-4 px-3 py-1 bg-primary/10 text-primary text-sm font-medium rounded-full border border-primary/20">
              After
            </div>
            <div className="bg-card border border-primary/20 rounded-xl p-6 font-mono text-sm">
              <div className="space-y-3">
                <div className="flex items-center gap-3 text-foreground">
                  <Folder className="w-4 h-4 text-primary" />
                  <span>Taylor Swift/</span>
                </div>
                <div className="pl-6 space-y-1">
                  <div className="flex items-center gap-3 text-muted-foreground">
                    <Folder className="w-4 h-4 text-primary/70" />
                    <span>1989 (Taylor{"'"}s Version)/</span>
                  </div>
                  <div className="pl-6 space-y-2">
                    <div className="flex items-center gap-3 text-foreground/80">
                      <Check className="w-4 h-4 text-primary" />
                      <span>01 - Welcome to New York.flac</span>
                    </div>
                    <div className="flex items-center gap-3 text-foreground/80">
                      <Check className="w-4 h-4 text-primary" />
                      <span>02 - Blank Space.flac</span>
                    </div>
                  </div>
                </div>
                <div className="flex items-center gap-3 text-foreground mt-4">
                  <Folder className="w-4 h-4 text-primary" />
                  <span>Billie Eilish/</span>
                </div>
                <div className="pl-6 space-y-1">
                  <div className="flex items-center gap-3 text-muted-foreground">
                    <Folder className="w-4 h-4 text-primary/70" />
                    <span>When We All Fall Asleep.../</span>
                  </div>
                  <div className="pl-6">
                    <div className="flex items-center gap-3 text-foreground/80">
                      <Check className="w-4 h-4 text-primary" />
                      <span>01 - bad guy.flac</span>
                    </div>
                  </div>
                </div>
                <div className="flex items-center gap-3 text-foreground mt-4">
                  <Folder className="w-4 h-4 text-primary" />
                  <span>The Weeknd/</span>
                </div>
                <div className="pl-6 space-y-1">
                  <div className="flex items-center gap-3 text-muted-foreground">
                    <Folder className="w-4 h-4 text-primary/70" />
                    <span>After Hours/</span>
                  </div>
                  <div className="pl-6">
                    <div className="flex items-center gap-3 text-foreground/80">
                      <Check className="w-4 h-4 text-primary" />
                      <span>09 - Blinding Lights.flac</span>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </section>
  )
}
