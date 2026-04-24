"use client"

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

import { cn } from "@/lib/utils"
import {
  Sidebar,
  SidebarContent,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarMenuSub,
  SidebarMenuSubButton,
  SidebarMenuSubItem,
  SidebarRail,
} from "@/components/ui/sidebar"

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

const enrichmentSources: { name: string; color: string }[] = [
  { name: "MusicBrainz", color: "bg-purple-500" },
  { name: "AcoustID", color: "bg-blue-500" },
  { name: "Spotify", color: "bg-green-500" },
  { name: "Last.fm", color: "bg-red-500" },
  { name: "LRCLIB", color: "bg-emerald-600" },
  { name: "Genius", color: "bg-yellow-500" },
  { name: "Cover Art Archive", color: "bg-fuchsia-500" },
]

export function AppSidebar() {
  const pathname = usePathname()
  const searchParams = useSearchParams()
  const onLibrary =
    pathname === "/app" || pathname.startsWith("/app/")
  const activeLibraryView: LibraryView = onLibrary
    ? ((searchParams.get("view") as LibraryView | null) ?? "albums")
    : "albums"

  return (
    <Sidebar collapsible="icon">
      <SidebarHeader>
        <SidebarMenu>
          <SidebarMenuItem>
            <SidebarMenuButton size="lg" asChild tooltip="MusicHoarder">
              <Link href="/overview">
                <div className="flex size-8 items-center justify-center rounded-lg bg-primary text-primary-foreground">
                  <Music className="size-4" />
                </div>
                <div className="flex min-w-0 flex-col leading-tight">
                  <span className="truncate font-semibold">MusicHoarder</span>
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
                          return (
                            <SidebarMenuSubItem key={view.value}>
                              <SidebarMenuSubButton
                                asChild
                                isActive={activeLibraryView === view.value}
                              >
                                <Link href={href}>{view.label}</Link>
                              </SidebarMenuSubButton>
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

        <SidebarGroup className="group-data-[collapsible=icon]:hidden">
          <SidebarGroupLabel>Enrichment sources</SidebarGroupLabel>
          <SidebarGroupContent>
            <ul className="flex flex-col gap-0.5 px-2 py-1 text-sm">
              {enrichmentSources.map((source) => (
                <li
                  key={source.name}
                  className="flex items-center gap-2 py-1 text-muted-foreground"
                >
                  <span
                    className={cn("size-2 shrink-0 rounded-full", source.color)}
                    aria-hidden
                  />
                  <span className="truncate">{source.name}</span>
                </li>
              ))}
            </ul>
          </SidebarGroupContent>
        </SidebarGroup>
      </SidebarContent>

      <SidebarRail />
    </Sidebar>
  )
}
