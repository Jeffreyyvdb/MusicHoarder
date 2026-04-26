"use client"

import { Suspense } from "react"

import { cn } from "@/lib/utils"
import { AppHeader } from "@/components/app-header"
import { AppSidebar } from "@/components/app-sidebar"
import {
  SidebarInset,
  SidebarProvider,
} from "@/components/ui/sidebar"
import { usePlayer } from "@/lib/player-context"

type AppShellProps = {
  children: React.ReactNode
  className?: string
}

export function AppShell({ children, className }: AppShellProps) {
  const { currentSong } = usePlayer()
  return (
    <SidebarProvider>
      <Suspense fallback={null}>
        <AppSidebar />
      </Suspense>
      <SidebarInset
        className={cn(
          "h-svh bg-background",
          currentSong && "pb-[60px] sm:pb-[68px]",
          className,
        )}
      >
        <AppHeader />
        {children}
      </SidebarInset>
    </SidebarProvider>
  )
}
