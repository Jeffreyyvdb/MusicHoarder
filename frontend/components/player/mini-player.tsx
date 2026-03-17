"use client"

import { Music, Pause, Play, Volume2, VolumeX, X } from "lucide-react"
import { usePlayer } from "@/lib/player-context"
import { Button } from "@/components/ui/button"
import { Slider } from "@/components/ui/slider"
import { cn } from "@/lib/utils"

function formatTime(seconds: number): string {
  if (!Number.isFinite(seconds) || Number.isNaN(seconds) || seconds < 0) return "0:00"
  const m = Math.floor(seconds / 60)
  const s = Math.floor(seconds % 60)
  return `${m}:${s.toString().padStart(2, "0")}`
}

export function MiniPlayer() {
  const { currentSong, isPlaying, currentTime, duration, volume, togglePlay, seek, setVolume, stop } =
    usePlayer()

  if (!currentSong) return null

  const progress = duration > 0 ? [currentTime] : [0]
  const maxDuration = duration > 0 ? duration : 1

  return (
    <div className="fixed bottom-0 left-0 right-0 z-50 border-t border-border bg-sidebar shadow-[0_-4px_20px_rgba(0,0,0,0.3)]">
      <div className="flex h-[64px] items-center gap-3 px-4">

        {/* Track info — fixed width, shrink-0 */}
        <div className="flex w-40 shrink-0 items-center gap-2 sm:w-48">
          <div className="flex size-8 shrink-0 items-center justify-center rounded-md bg-primary/20">
            <Music className="size-4 text-primary" />
          </div>
          <div className="min-w-0">
            <p className="truncate text-sm font-medium leading-tight">{currentSong.title}</p>
            <p className="truncate text-xs text-muted-foreground leading-tight">{currentSong.artist}</p>
          </div>
        </div>

        {/* Play / Pause */}
        <Button
          variant="ghost"
          size="icon"
          className="size-8 shrink-0 text-foreground hover:text-primary hover:bg-primary/10"
          onClick={togglePlay}
          aria-label={isPlaying ? "Pause" : "Play"}
        >
          {isPlaying ? (
            <Pause className="size-4" />
          ) : (
            <Play className="size-4 translate-x-px" />
          )}
        </Button>

        {/* Current time */}
        <span className="w-9 shrink-0 text-right text-xs tabular-nums text-muted-foreground">
          {formatTime(currentTime)}
        </span>

        {/* Seek bar — takes all remaining space */}
        <Slider
          value={progress}
          max={maxDuration}
          min={0}
          step={1}
          className="flex-1 min-w-0 cursor-pointer"
          onValueChange={([val]) => {
            if (val !== undefined) seek(val)
          }}
          aria-label="Seek"
        />

        {/* Duration */}
        <span className="w-9 shrink-0 text-xs tabular-nums text-muted-foreground">
          {formatTime(duration)}
        </span>

        {/* Volume — hidden on mobile */}
        <div className="hidden shrink-0 items-center gap-1 sm:flex">
          <Button
            variant="ghost"
            size="icon"
            className="size-8 text-muted-foreground hover:text-foreground"
            onClick={() => setVolume(volume === 0 ? 0.8 : 0)}
            aria-label={volume === 0 ? "Unmute" : "Mute"}
          >
            {volume === 0 ? (
              <VolumeX className="size-4" />
            ) : (
              <Volume2 className="size-4" />
            )}
          </Button>
          <Slider
            value={[volume]}
            max={1}
            min={0}
            step={0.02}
            className="w-20 shrink-0 cursor-pointer"
            onValueChange={([val]) => {
              if (val !== undefined) setVolume(val)
            }}
            aria-label="Volume"
          />
        </div>

        {/* Close */}
        <Button
          variant="ghost"
          size="icon"
          className="size-8 shrink-0 text-muted-foreground hover:text-foreground"
          onClick={stop}
          aria-label="Close player"
        >
          <X className="size-4" />
        </Button>

      </div>
    </div>
  )
}
