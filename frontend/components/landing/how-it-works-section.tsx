import { Search, AudioWaveform, Sparkles, FolderTree } from "lucide-react"

const steps = [
  {
    icon: Search,
    step: "1",
    title: "Scan",
    description: "Point at your NAS or local drive. Discovers every .mp3, .flac, and audio file automatically.",
  },
  {
    icon: AudioWaveform,
    step: "2",
    title: "Fingerprint",
    description: "Identifies tracks by audio content using AcoustID, not just unreliable file tags.",
  },
  {
    icon: Sparkles,
    step: "3",
    title: "Enrich",
    description: "Matches against MusicBrainz, Spotify, and community databases for complete metadata.",
  },
  {
    icon: FolderTree,
    step: "4",
    title: "Build",
    description: "Creates a clean library with correct tags, album art, and organized folder structure.",
  },
]

export function HowItWorksSection() {
  return (
    <section className="py-24 px-6 bg-card/30 border-t border-border">
      <div className="max-w-6xl mx-auto">
        <div className="text-center mb-16">
          <h2 className="text-3xl md:text-4xl font-bold mb-4">
            How it works
          </h2>
          <p className="text-muted-foreground text-lg max-w-xl mx-auto">
            Four simple steps from chaos to a perfectly organized music library.
          </p>
        </div>
        
        <div className="grid sm:grid-cols-2 lg:grid-cols-4 gap-6 lg:gap-8">
          {steps.map((step) => (
            <div 
              key={step.step}
              className="group relative bg-card border border-border rounded-xl p-6 hover:border-primary/50 transition-colors"
            >
              <div className="absolute -top-3 -right-3 w-8 h-8 bg-primary text-primary-foreground rounded-full flex items-center justify-center text-sm font-bold">
                {step.step}
              </div>
              <div className="w-12 h-12 bg-primary/10 rounded-lg flex items-center justify-center mb-4 group-hover:bg-primary/20 transition-colors">
                <step.icon className="w-6 h-6 text-primary" />
              </div>
              <h3 className="text-lg font-semibold mb-2">{step.title}</h3>
              <p className="text-muted-foreground text-sm leading-relaxed">
                {step.description}
              </p>
            </div>
          ))}
        </div>
      </div>
    </section>
  )
}
