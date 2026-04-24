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

import { ThemeToggle } from "@/components/theme-toggle"
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
      </SidebarContent>

      <SidebarFooter>
        <SidebarMenu>
          <SidebarMenuItem>
            <div className="flex items-center justify-between gap-2 px-2 py-1 group-data-[collapsible=icon]:justify-center group-data-[collapsible=icon]:px-0">
              <span className="text-xs text-muted-foreground group-data-[collapsible=icon]:hidden">
                Theme
              </span>
              <ThemeToggle />
            </div>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarFooter>

      <SidebarRail />
    </Sidebar>
  )
}
