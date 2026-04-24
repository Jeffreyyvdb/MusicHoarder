"use client"

import { useEffect, useState } from "react"
import Link from "next/link"
import { usePathname, useSearchParams } from "next/navigation"
import {
  FileWarning,
  FolderOpen,
  LayoutDashboard,
  Music,
  Music2,
  Settings,
  Users,
} from "lucide-react"

import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarMenuSub,
  SidebarMenuSubItem,
  SidebarRail,
} from "@/components/ui/sidebar"
import {
  fetchOverview,
  fetchStats,
  type ApiOverview,
  type ApiStats,
} from "@/lib/api-client"

type LibraryView = "albums" | "source" | "destination"

const libraryViews: { value: LibraryView; label: string }[] = [
  { value: "albums", label: "Albums" },
  { value: "source", label: "Source" },
  { value: "destination", label: "Destination" },
]

const navItems = [
  { href: "/overview", label: "Overview", icon: LayoutDashboard },
  { href: "/app", label: "Library", icon: FolderOpen },
  { href: "/artists", label: "Artists", icon: Users },
  { href: "/spotify", label: "Spotify", icon: Music2 },
  { href: "/review", label: "Review", icon: FileWarning },
  { href: "/settings", label: "Settings", icon: Settings },
]

function formatLibrarySize(bytes: number): string {
  const gib = bytes / (1024 * 1024 * 1024)
  if (gib >= 1) return `${gib.toFixed(1)} GB`
  const mib = bytes / (1024 * 1024)
  return `${mib.toFixed(0)} MB`
}

export function AppSidebar() {
  const pathname = usePathname()
  const searchParams = useSearchParams()
  const onLibrary =
    pathname === "/app" || pathname.startsWith("/app/")
  const activeLibraryView: LibraryView = onLibrary
    ? ((searchParams.get("view") as LibraryView | null) ?? "albums")
    : "albums"

  const [overview, setOverview] = useState<ApiOverview | null>(null)
  const [stats, setStats] = useState<ApiStats | null>(null)

  useEffect(() => {
    let cancelled = false
    void (async () => {
      try {
        const [ov, st] = await Promise.all([fetchOverview(), fetchStats()])
        if (!cancelled) {
          setOverview(ov)
          setStats(st)
        }
      } catch {
        // Silently ignore — sidebar captions are a progressive enhancement.
      }
    })()
    return () => {
      cancelled = true
    }
  }, [])

  const pathsByView: Record<LibraryView, string | null> = {
    albums: null,
    source: overview?.sourcePath ?? null,
    destination: overview?.destinationPath ?? null,
  }

  const totalBytes = stats?.storage?.totalBytes ?? null
  const totalTracks = stats?.tracks?.total ?? null

  return (
    <Sidebar collapsible="icon">
      <SidebarHeader>
        <SidebarMenu>
          <SidebarMenuItem>
            <SidebarMenuButton size="lg" asChild tooltip="MusicHoarder">
              <Link href="/overview">
                <div className="flex aspect-square size-8 shrink-0 items-center justify-center rounded-lg bg-primary text-primary-foreground">
                  <Music className="size-4" />
                </div>
                <div className="grid min-w-0 flex-1 text-left leading-tight">
                  <span className="truncate text-sm font-semibold">MusicHoarder</span>
                  <span className="truncate text-xs text-muted-foreground">
                    Library manager
                  </span>
                </div>
              </Link>
            </SidebarMenuButton>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarHeader>

      <SidebarContent>
        <SidebarGroup>
          <SidebarGroupLabel>Navigation</SidebarGroupLabel>
          <SidebarGroupContent>
            <SidebarMenu>
              {navItems.map((item) => {
                const isActive =
                  item.href === "/app"
                    ? onLibrary
                    : pathname.startsWith(item.href)

                return (
                  <SidebarMenuItem key={item.href}>
                    <SidebarMenuButton
                      asChild
                      isActive={isActive}
                      tooltip={item.label}
                    >
                      <Link href={item.href}>
                        <item.icon className="size-4" />
                        <span>{item.label}</span>
                      </Link>
                    </SidebarMenuButton>

                    {item.href === "/app" && onLibrary && (
                      <SidebarMenuSub>
                        {libraryViews.map((view) => {
                          const href =
                            view.value === "albums"
                              ? "/app"
                              : `/app?view=${view.value}`
                          const isActiveView = activeLibraryView === view.value
                          const path = pathsByView[view.value]
                          return (
                            <SidebarMenuSubItem key={view.value}>
                              <Link
                                href={href}
                                data-active={isActiveView || undefined}
                                className="flex min-w-0 flex-col gap-0.5 rounded-md px-2 py-1.5 text-sm text-sidebar-foreground/80 outline-hidden transition-colors hover:bg-sidebar-accent hover:text-sidebar-accent-foreground focus-visible:ring-2 focus-visible:ring-sidebar-ring data-[active=true]:bg-sidebar-accent data-[active=true]:font-medium data-[active=true]:text-sidebar-accent-foreground"
                              >
                                <span className="truncate">{view.label}</span>
                                {path && (
                                  <span
                                    className="truncate font-mono text-[10px] leading-tight text-muted-foreground"
                                    title={path}
                                  >
                                    {path}
                                  </span>
                                )}
                              </Link>
                            </SidebarMenuSubItem>
                          )
                        })}
                      </SidebarMenuSub>
                    )}
                  </SidebarMenuItem>
                )
              })}
            </SidebarMenu>
          </SidebarGroupContent>
        </SidebarGroup>
      </SidebarContent>

      {(totalBytes !== null || totalTracks !== null) && (
        <SidebarFooter className="group-data-[collapsible=icon]:hidden">
          <div className="rounded-lg bg-sidebar-accent/60 p-3">
            <div className="flex items-center gap-2 text-sm">
              <FolderOpen className="size-4 text-primary" />
              <span className="font-medium">Library Size</span>
            </div>
            {totalBytes !== null && (
              <p className="mt-1 text-2xl font-bold">
                {formatLibrarySize(totalBytes)}
              </p>
            )}
            {totalTracks !== null && (
              <p className="text-xs text-muted-foreground">
                {totalTracks.toLocaleString()} tracks total
              </p>
            )}
          </div>
        </SidebarFooter>
      )}

      <SidebarRail />
    </Sidebar>
  )
}
