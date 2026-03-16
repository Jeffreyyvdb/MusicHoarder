"use client"

import { Button } from "@/components/ui/button"
import { Github, Star } from "lucide-react"

export function HeroSection() {
  return (
    <section className="relative min-h-screen flex flex-col items-center justify-center px-6 py-24 overflow-hidden">
      {/* Background grid effect */}
      <div className="absolute inset-0 bg-[linear-gradient(rgba(255,255,255,0.02)_1px,transparent_1px),linear-gradient(90deg,rgba(255,255,255,0.02)_1px,transparent_1px)] bg-[size:64px_64px]" />
      
      {/* Gradient orbs */}
      <div className="absolute top-1/4 left-1/4 w-96 h-96 bg-primary/10 rounded-full blur-3xl" />
      <div className="absolute bottom-1/4 right-1/4 w-96 h-96 bg-accent/5 rounded-full blur-3xl" />
      
      <div className="relative z-10 max-w-4xl mx-auto text-center">
        <div className="inline-flex items-center gap-2 px-4 py-2 mb-8 text-sm border border-border rounded-full bg-card/50 backdrop-blur-sm">
          <span className="w-2 h-2 bg-primary rounded-full animate-pulse" />
          <span className="text-muted-foreground">Self-hosted & Open Source</span>
        </div>
        
        <h1 className="text-4xl md:text-6xl lg:text-7xl font-bold tracking-tight mb-6 text-balance">
          Your music library is a mess.
          <span className="block text-primary mt-2">MusicHoarder fixes it.</span>
        </h1>
        
        <p className="text-lg md:text-xl text-muted-foreground max-w-2xl mx-auto mb-12 leading-relaxed text-pretty">
          Wrong titles, missing albums, files named {'"'}track01_final_FINAL.mp3{'"'}. 
          MusicHoarder identifies, enriches, and organizes your entire collection automatically.
        </p>
        
        <div className="flex flex-col sm:flex-row items-center justify-center gap-4">
          <Button size="lg" className="gap-2 px-8 h-12 text-base font-medium" asChild>
            <a href="https://github.com/Jeffreyyvdb/MusicHoarder" target="_blank" rel="noopener noreferrer">
              <Star className="w-5 h-5" />
              Star on GitHub
            </a>
          </Button>
          <Button size="lg" variant="outline" className="gap-2 px-8 h-12 text-base font-medium" asChild>
            <a href="/app">
              Launch App
            </a>
          </Button>
        </div>
        
        <div className="flex items-center justify-center gap-8 mt-12 text-sm text-muted-foreground">
          <div className="flex items-center gap-2">
            <Github className="w-4 h-4" />
            <span>MIT License</span>
          </div>
          <div className="flex items-center gap-2">
            <span className="w-1.5 h-1.5 bg-primary rounded-full" />
            <span>.NET + Next.js</span>
          </div>
          <div className="flex items-center gap-2">
            <span className="w-1.5 h-1.5 bg-primary rounded-full" />
            <span>Docker Ready</span>
          </div>
        </div>
      </div>
    </section>
  )
}
