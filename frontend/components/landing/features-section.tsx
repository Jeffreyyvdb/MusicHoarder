import { Headphones, Music2, FileText, ClipboardCheck, Server, Shield } from "lucide-react"

const features = [
  {
    icon: Music2,
    title: "Unreleased & Leaked Music",
    description: "Works with unofficial releases, leaks, and bootlegs using community tracker matching.",
  },
  {
    icon: Headphones,
    title: "Ear Verification",
    description: "Built-in player to confirm matches before committing. Never silently wrong.",
  },
  {
    icon: FileText,
    title: "Synced Lyrics",
    description: "LRC lyrics embedded automatically via LRCLIB. No extra setup required.",
  },
  {
    icon: ClipboardCheck,
    title: "Review Queue",
    description: "Low-confidence matches flagged for human review. You stay in control.",
  },
  {
    icon: Server,
    title: "Self-Hosted",
    description: "Your files never leave your machine. Runs on TrueNAS, Proxmox, or any homelab.",
  },
  {
    icon: Shield,
    title: "Open Source",
    description: "MIT licensed. Full transparency, community-driven development.",
  },
]

export function FeaturesSection() {
  return (
    <section className="py-24 px-6 border-t border-border">
      <div className="max-w-6xl mx-auto">
        <div className="text-center mb-16">
          <h2 className="text-3xl md:text-4xl font-bold mb-4">
            Built for music hoarders
          </h2>
          <p className="text-muted-foreground text-lg max-w-xl mx-auto">
            Everything you need to wrangle even the messiest music collection.
          </p>
        </div>
        
        <div className="grid sm:grid-cols-2 lg:grid-cols-3 gap-6">
          {features.map((feature) => (
            <div 
              key={feature.title}
              className="group p-6 rounded-xl border border-border bg-card/50 hover:bg-card hover:border-border transition-all"
            >
              <div className="w-10 h-10 bg-primary/10 rounded-lg flex items-center justify-center mb-4 group-hover:bg-primary/20 transition-colors">
                <feature.icon className="w-5 h-5 text-primary" />
              </div>
              <h3 className="text-lg font-semibold mb-2">{feature.title}</h3>
              <p className="text-muted-foreground text-sm leading-relaxed">
                {feature.description}
              </p>
            </div>
          ))}
        </div>
      </div>
    </section>
  )
}
