import { Container, Database, Cpu, Globe } from "lucide-react"

const techItems = [
  { icon: Cpu, label: ".NET 9 API" },
  { icon: Globe, label: "Next.js Frontend" },
  { icon: Database, label: "SQLite / PostgreSQL" },
  { icon: Container, label: "Docker Ready" },
]

export function TechStackSection() {
  return (
    <section className="py-16 px-6 bg-card/30 border-t border-border">
      <div className="max-w-4xl mx-auto">
        <div className="flex flex-col md:flex-row items-center justify-between gap-8">
          <div>
            <h3 className="text-xl font-semibold mb-2">Built for homelabs</h3>
            <p className="text-muted-foreground text-sm">
              Modern stack designed for self-hosting enthusiasts.
            </p>
          </div>
          
          <div className="flex flex-wrap items-center justify-center gap-6">
            {techItems.map((item) => (
              <div 
                key={item.label}
                className="flex items-center gap-2 px-4 py-2 bg-card border border-border rounded-lg"
              >
                <item.icon className="w-4 h-4 text-primary" />
                <span className="text-sm font-medium">{item.label}</span>
              </div>
            ))}
          </div>
        </div>
      </div>
    </section>
  )
}
