"use client"

import { Suspense } from "react"

import { cn } from "@/lib/utils"
import { AppHeader } from "@/components/app-header"
import { AppSidebar } from "@/components/app-sidebar"
import {
  SidebarInset,
  SidebarProvider,
} from "@/components/ui/sidebar"

type AppShellProps = {
  children: React.ReactNode
  className?: string
}

export function AppShell({ children, className }: AppShellProps) {
  return (
    <SidebarProvider>
      <Suspense fallback={null}>
        <AppSidebar />
      </Suspense>
      <SidebarInset className={cn("h-svh bg-background", className)}>
        <AppHeader />
        {children}
      </SidebarInset>
    </SidebarProvider>
  )
}
