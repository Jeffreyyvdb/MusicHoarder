"use client"

import { PlayerProvider } from "@/lib/player-context"
import { MiniPlayer } from "./mini-player"

export function PlayerProviderWrapper({ children }: { children: React.ReactNode }) {
  return (
    <PlayerProvider>
      {children}
      <MiniPlayer />
    </PlayerProvider>
  )
}
