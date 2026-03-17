"use client"

import { Folder, Music, CheckCircle2, Clock, AlertCircle, Loader2, Eye, Pause, Play } from "lucide-react"
import { cn } from "@/lib/utils"
import type { FileItem } from "@/lib/types"
import { usePlayer } from "@/lib/player-context"
import { getSongStreamUrl, parseSongId } from "@/lib/api-client"

interface FileGridProps {
  items: FileItem[]
  selectedId: string | null
  onSelect: (item: FileItem) => void
  onOpen: (item: FileItem) => void
  viewMode: "grid" | "list"
  emptyMessage?: string
}

export function FileGrid({
  items,
  selectedId,
  onSelect,
  onOpen,
  viewMode,
  emptyMessage = "This folder is empty",
}: FileGridProps) {
  const { currentSong, isPlaying, playSong } = usePlayer()

  if (items.length === 0) {
    return (
      <div className="flex flex-1 items-center justify-center text-muted-foreground">
        <div className="text-center">
          <Folder className="mx-auto size-12 opacity-50" />
          <p className="mt-2">{emptyMessage}</p>
        </div>
      </div>
    )
  }

  if (viewMode === "list") {
    return (
      <div className="divide-y divide-border">
        {items.map((item) => {
          const songId = parseSongId(item.id)
          const isCurrentlyPlaying = currentSong?.id === songId && isPlaying
          const isCurrentlyLoaded = currentSong?.id === songId
          return (
            <FileListItem
              key={item.id}
              item={item}
              isSelected={selectedId === item.id}
              isPlaying={isCurrentlyPlaying}
              isLoaded={isCurrentlyLoaded}
              onSelect={() => onSelect(item)}
              onOpen={() => onOpen(item)}
              onPlay={
                songId !== null && item.type === "audio"
                  ? () =>
                      playSong({
                        id: songId,
                        title: item.metadata?.title ?? item.name,
                        artist: item.metadata?.artist ?? "Unknown Artist",
                        streamUrl: getSongStreamUrl(songId),
                      })
                  : undefined
              }
            />
          )
        })}
      </div>
    )
  }

  return (
    <div className="grid grid-cols-2 gap-3 p-4 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6">
      {items.map((item) => {
        const songId = parseSongId(item.id)
        const isCurrentlyPlaying = currentSong?.id === songId && isPlaying
        const isCurrentlyLoaded = currentSong?.id === songId
        return (
          <FileGridItem
            key={item.id}
            item={item}
            isSelected={selectedId === item.id}
            isPlaying={isCurrentlyPlaying}
            isLoaded={isCurrentlyLoaded}
            onSelect={() => onSelect(item)}
            onOpen={() => onOpen(item)}
            onPlay={
              songId !== null && item.type === "audio"
                ? () =>
                    playSong({
                      id: songId,
                      title: item.metadata?.title ?? item.name,
                      artist: item.metadata?.artist ?? "Unknown Artist",
                      streamUrl: getSongStreamUrl(songId),
                    })
                : undefined
            }
          />
        )
      })}
    </div>
  )
}

interface FileItemProps {
  item: FileItem
  isSelected: boolean
  isPlaying: boolean
  isLoaded: boolean
  onSelect: () => void
  onOpen: () => void
  onPlay?: () => void
}

function FileGridItem({ item, isSelected, isPlaying, isLoaded, onSelect, onOpen, onPlay }: FileItemProps) {
  const isFolder = item.type === "folder"
  const status = item.metadata?.enrichmentStatus

  return (
    // Must be a div, not a button, because it contains <button> play overlays.
    // role="button" + tabIndex preserve full keyboard accessibility.
    <div
      role="button"
      tabIndex={0}
      onClick={onSelect}
      onDoubleClick={onOpen}
      onKeyDown={(e) => {
        if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onSelect() }
      }}
      className={cn(
        "group relative flex cursor-pointer flex-col items-center gap-2 rounded-lg p-3 text-center transition-all",
        "hover:bg-secondary/50",
        isSelected && "bg-primary/10 ring-1 ring-primary",
        isLoaded && !isSelected && "bg-primary/5"
      )}
    >
      <div className="relative">
        {isFolder ? (
          <Folder className="size-12 text-primary" />
        ) : (
          <>
            {item.metadata?.albumArt ? (
              <div className="relative size-16 overflow-hidden rounded-md">
                <img
                  src={item.metadata.albumArt}
                  alt={item.metadata.album}
                  className="size-full object-cover"
                  crossOrigin="anonymous"
                />
                {/* Play overlay */}
                {onPlay && (
                  <div
                    className={cn(
                      "absolute inset-0 flex items-center justify-center bg-black/50 transition-opacity",
                      isPlaying ? "opacity-100" : "opacity-0 group-hover:opacity-100"
                    )}
                  >
                    <button
                      onClick={(e) => { e.stopPropagation(); onPlay() }}
                      className="flex size-8 items-center justify-center rounded-full bg-primary text-primary-foreground shadow-lg transition-transform hover:scale-110 active:scale-95"
                      aria-label={isPlaying ? "Pause" : "Play"}
                    >
                      {isPlaying ? (
                        <Pause className="size-3.5" />
                      ) : (
                        <Play className="size-3.5 translate-x-px" />
                      )}
                    </button>
                  </div>
                )}
              </div>
            ) : (
              <div
                className={cn(
                  "relative flex size-16 items-center justify-center rounded-md bg-secondary transition-colors",
                  isLoaded && "bg-primary/20"
                )}
              >
                <Music
                  className={cn(
                    "size-8 transition-colors",
                    isLoaded ? "text-primary" : "text-muted-foreground"
                  )}
                />
                {/* Play overlay on non-art tracks */}
                {onPlay && (
                  <div
                    className={cn(
                      "absolute inset-0 flex items-center justify-center rounded-md bg-black/50 transition-opacity",
                      isPlaying ? "opacity-100" : "opacity-0 group-hover:opacity-100"
                    )}
                  >
                    <button
                      onClick={(e) => { e.stopPropagation(); onPlay() }}
                      className="flex size-8 items-center justify-center rounded-full bg-primary text-primary-foreground shadow-lg transition-transform hover:scale-110 active:scale-95"
                      aria-label={isPlaying ? "Pause" : "Play"}
                    >
                      {isPlaying ? (
                        <Pause className="size-3.5" />
                      ) : (
                        <Play className="size-3.5 translate-x-px" />
                      )}
                    </button>
                  </div>
                )}
              </div>
            )}
            {status && (
              <div className="absolute -bottom-1 -right-1">
                <StatusIcon status={status} />
              </div>
            )}
            {/* Playing indicator dot */}
            {isPlaying && (
              <div className="absolute -top-1 -left-1 size-2.5 rounded-full bg-primary ring-2 ring-background animate-pulse" />
            )}
          </>
        )}
      </div>
      <div className="w-full">
        <p className={cn("truncate text-sm font-medium", isLoaded && "text-primary")}>{item.name}</p>
        {!isFolder && item.metadata && (
          <p className="truncate text-xs text-muted-foreground">
            {item.metadata.artist}
          </p>
        )}
      </div>
    </div>
  )
}

function FileListItem({ item, isSelected, isPlaying, isLoaded, onSelect, onOpen, onPlay }: FileItemProps) {
  const isFolder = item.type === "folder"
  const status = item.metadata?.enrichmentStatus

  return (
    <div
      className={cn(
        "group flex w-full items-center gap-3 px-4 py-2 transition-colors",
        "hover:bg-secondary/50",
        isSelected && "bg-primary/10",
        isLoaded && !isSelected && "bg-primary/5"
      )}
    >
      {/* Play button for audio tracks */}
      {!isFolder && onPlay ? (
        <button
          onClick={onPlay}
          className={cn(
            "flex size-8 shrink-0 items-center justify-center rounded-full transition-all",
            isLoaded
              ? "bg-primary text-primary-foreground"
              : "bg-secondary text-muted-foreground opacity-0 group-hover:opacity-100 hover:bg-primary/20 hover:text-primary"
          )}
          aria-label={isPlaying ? "Pause" : "Play"}
        >
          {isPlaying ? (
            <Pause className="size-3.5" />
          ) : (
            <Play className="size-3.5 translate-x-px" />
          )}
        </button>
      ) : (
        <div className="size-8 shrink-0" />
      )}

      {/* Main item content — clicking selects the item */}
      <button
        onClick={onSelect}
        onDoubleClick={onOpen}
        className="flex min-w-0 flex-1 items-center gap-3 text-left"
      >
        <div className="relative shrink-0">
          {isFolder ? (
            <Folder className="size-8 text-primary" />
          ) : item.metadata?.albumArt ? (
            <div className="size-10 overflow-hidden rounded">
              <img
                src={item.metadata.albumArt}
                alt={item.metadata.album}
                className="size-full object-cover"
                crossOrigin="anonymous"
              />
            </div>
          ) : (
            <div
              className={cn(
                "flex size-10 items-center justify-center rounded transition-colors",
                isLoaded ? "bg-primary/20" : "bg-secondary"
              )}
            >
              <Music
                className={cn(
                  "size-5 transition-colors",
                  isLoaded ? "text-primary" : "text-muted-foreground"
                )}
              />
            </div>
          )}
        </div>

        <div className="min-w-0 flex-1">
          <p className={cn("truncate font-medium", isLoaded && "text-primary")}>{item.name}</p>
          {!isFolder && item.metadata && (
            <p className="truncate text-sm text-muted-foreground">
              {item.metadata.artist} - {item.metadata.album}
            </p>
          )}
        </div>

        {!isFolder && item.metadata && (
          <>
            <span className="hidden text-sm text-muted-foreground sm:block">
              {item.metadata.format}
            </span>
            <span className="hidden text-sm text-muted-foreground md:block">
              {formatDuration(item.metadata.duration)}
            </span>
            <span className="hidden text-sm text-muted-foreground lg:block">
              {formatFileSize(item.metadata.fileSize)}
            </span>
            {status && <StatusIcon status={status} />}
          </>
        )}
      </button>
    </div>
  )
}

function StatusIcon({ status }: { status: string }) {
  switch (status) {
    case "complete":
      return <CheckCircle2 className="size-4 text-primary" />
    case "processing":
      return <Loader2 className="size-4 animate-spin text-chart-2" />
    case "needsreview":
      return <Eye className="size-4 text-amber-600 dark:text-amber-500" />
    case "pending":
      return <Clock className="size-4 text-muted-foreground" />
    case "failed":
      return <AlertCircle className="size-4 text-destructive" />
    default:
      return null
  }
}

function formatDuration(seconds: number): string {
  const mins = Math.floor(seconds / 60)
  const secs = seconds % 60
  return `${mins}:${secs.toString().padStart(2, "0")}`
}

function formatFileSize(bytes: number): string {
  const mb = bytes / (1024 * 1024)
  return `${mb.toFixed(1)} MB`
}
