import { Button } from "@/components/ui/button"
import { Star, Github } from "lucide-react"

export function CTASection() {
  return (
    <section className="py-24 px-6 border-t border-border">
      <div className="max-w-3xl mx-auto text-center">
        <h2 className="text-3xl md:text-4xl font-bold mb-4">
          Ready to organize your library?
        </h2>
        <p className="text-muted-foreground text-lg mb-8 max-w-xl mx-auto">
          Stop fighting with messy filenames. Let MusicHoarder do the heavy lifting.
        </p>
        
        <div className="flex flex-col sm:flex-row items-center justify-center gap-4">
          <Button size="lg" className="gap-2 px-8 h-12 text-base font-medium" asChild>
            <a href="https://github.com/Jeffreyyvdb/MusicHoarder" target="_blank" rel="noopener noreferrer">
              <Star className="w-5 h-5" />
              Star on GitHub
            </a>
          </Button>
          <Button size="lg" variant="outline" className="gap-2 px-8 h-12 text-base font-medium" asChild>
            <a href="https://github.com/Jeffreyyvdb/MusicHoarder" target="_blank" rel="noopener noreferrer">
              <Github className="w-5 h-5" />
              View Documentation
            </a>
          </Button>
        </div>
      </div>
    </section>
  )
}
