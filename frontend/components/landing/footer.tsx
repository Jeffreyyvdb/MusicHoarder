import { Github, Heart } from "lucide-react"

export function Footer() {
  return (
    <footer className="py-8 px-6 border-t border-border">
      <div className="max-w-6xl mx-auto flex flex-col md:flex-row items-center justify-between gap-4">
        <div className="flex items-center gap-2 text-sm text-muted-foreground">
          <span>Built with</span>
          <Heart className="w-4 h-4 text-primary fill-primary" />
          <span>by</span>
          <a 
            href="https://github.com/Jeffreyyvdb" 
            target="_blank" 
            rel="noopener noreferrer"
            className="text-foreground hover:text-primary transition-colors"
          >
            @Jeffreyyvdb
          </a>
        </div>
        
        <div className="flex items-center gap-6 text-sm text-muted-foreground">
          <a 
            href="https://github.com/Jeffreyyvdb/MusicHoarder" 
            target="_blank" 
            rel="noopener noreferrer"
            className="flex items-center gap-2 hover:text-foreground transition-colors"
          >
            <Github className="w-4 h-4" />
            <span>GitHub</span>
          </a>
          <span>MIT License</span>
        </div>
      </div>
    </footer>
  )
}
