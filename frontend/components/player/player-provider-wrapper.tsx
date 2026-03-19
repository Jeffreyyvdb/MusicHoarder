"use client"

import { PlayerProvider } from "@/lib/player-context"
import { usePlayer } from "@/lib/player-context"
import { MiniPlayer } from "./mini-player"

/**
 * Reserves space in the normal document flow equal to the MiniPlayer bar height.
 * Without this, the fixed MiniPlayer would overlay the bottom content of any
 * page that scrolls (e.g. Overview), making the last ~64 px inaccessible.
 */
function PlayerSpacer() {
  const { currentSong } = usePlayer()
  if (!currentSong) return null
  return <div className="h-14 sm:h-16 shrink-0" aria-hidden="true" />
}

export function PlayerProviderWrapper({ children }: { children: React.ReactNode }) {
  return (
    <PlayerProvider>
      {children}
      <PlayerSpacer />
      <MiniPlayer />
    </PlayerProvider>
  )
}
