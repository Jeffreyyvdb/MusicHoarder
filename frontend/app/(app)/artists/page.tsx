"use client"

import { useState } from "react"
import Link from "next/link"
import { Input } from "@/components/ui/input"
import { ScrollArea } from "@/components/ui/scroll-area"
import { mockArtists } from "@/lib/mock-data"
import { Search, Disc, Music, ChevronRight } from "lucide-react"

export default function ArtistsPage() {
  const [searchQuery, setSearchQuery] = useState("")

  const filteredArtists = mockArtists.filter((artist) =>
    artist.name.toLowerCase().includes(searchQuery.toLowerCase())
  )

  return (
      <div className="flex min-h-0 flex-1 flex-col overflow-hidden">
        {/* Page Header */}
        <div className="border-b border-border bg-card/30 px-4 py-6 md:px-6">
          <h1 className="text-2xl font-bold">Artists</h1>
          <p className="text-sm text-muted-foreground mt-1">
            Compare your library with full artist discographies
          </p>
        </div>

        {/* Search */}
        <div className="border-b border-border px-4 py-3 md:px-6">
          <div className="relative max-w-md">
            <Search className="absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
            <Input
              placeholder="Search artists..."
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              className="pl-9 bg-secondary border-0"
            />
          </div>
        </div>

        {/* Artist Grid */}
        <ScrollArea className="min-h-0 flex-1">
          <div className="grid grid-cols-1 gap-4 p-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 md:p-6">
            {filteredArtists.map((artist) => {
              const completionPercent = Math.round(
                (artist.tracksInLibrary / artist.totalTracks) * 100
              )

              return (
                <Link
                  key={artist.id}
                  href={`/artists/${artist.id}`}
                  className="group relative overflow-hidden rounded-xl bg-card border border-border transition-all hover:border-primary/50 hover:shadow-lg"
                >
                  {/* Artist Image */}
                  <div className="aspect-square overflow-hidden bg-secondary">
                    {artist.image ? (
                      <img
                        src={artist.image}
                        alt={artist.name}
                        className="size-full object-cover transition-transform group-hover:scale-105"
                        crossOrigin="anonymous"
                      />
                    ) : (
                      <div className="flex size-full items-center justify-center">
                        <Music className="size-16 text-muted-foreground" />
                      </div>
                    )}
                  </div>

                  {/* Artist Info */}
                  <div className="p-4">
                    <div className="flex items-center justify-between">
                      <h3 className="font-semibold truncate">{artist.name}</h3>
                      <ChevronRight className="size-4 text-muted-foreground opacity-0 transition-opacity group-hover:opacity-100" />
                    </div>
                    <p className="text-sm text-muted-foreground truncate">
                      {artist.genres.join(", ")}
                    </p>

                    {/* Stats */}
                    <div className="mt-3 flex items-center gap-4 text-xs text-muted-foreground">
                      <div className="flex items-center gap-1">
                        <Disc className="size-3" />
                        <span>
                          {artist.albumsInLibrary}/{artist.totalAlbums} albums
                        </span>
                      </div>
                      <div className="flex items-center gap-1">
                        <Music className="size-3" />
                        <span>
                          {artist.tracksInLibrary}/{artist.totalTracks} tracks
                        </span>
                      </div>
                    </div>

                    {/* Progress Bar */}
                    <div className="mt-3">
                      <div className="flex items-center justify-between text-xs mb-1">
                        <span className="text-muted-foreground">Library completion</span>
                        <span className="font-medium text-primary">{completionPercent}%</span>
                      </div>
                      <div className="h-1.5 rounded-full bg-secondary overflow-hidden">
                        <div
                          className="h-full rounded-full bg-primary transition-all"
                          style={{ width: `${completionPercent}%` }}
                        />
                      </div>
                    </div>
                  </div>
                </Link>
              )
            })}
          </div>
        </ScrollArea>
      </div>
  )
}
