"use client"

import Link from "next/link"
import { usePathname } from "next/navigation"
import {
  Music,
  LayoutDashboard,
  FolderOpen,
  FileWarning,
  Users,
  Music2,
  Settings,
} from "lucide-react"
import { cn } from "@/lib/utils"
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
} from "@/components/ui/sidebar"

const navItems = [
  { href: "/overview", label: "Overview", icon: LayoutDashboard },
  { href: "/app", label: "Library", icon: FolderOpen },
  { href: "/artists", label: "Artists", icon: Users },
  { href: "/spotify", label: "Spotify", icon: Music2 },
  { href: "/review", label: "Review", icon: FileWarning },
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

function isNavActive(pathname: string, href: string) {
  if (href === "/app") {
    return pathname === "/app" || pathname.startsWith("/app/")
  }
  return pathname.startsWith(href)
}

export function AppSidebar() {
  const pathname = usePathname()

  return (
    <Sidebar collapsible="icon">
      <SidebarHeader>
        <Link href="/overview" className="flex items-center gap-2 px-2 py-1.5">
          <div className="flex size-8 shrink-0 items-center justify-center rounded-lg bg-primary">
            <Music className="size-4 text-primary-foreground" />
          </div>
          <div className="flex min-w-0 flex-col group-data-[collapsible=icon]:hidden">
            <span className="truncate text-sm font-semibold leading-tight">MusicHoarder</span>
            <span className="truncate text-xs text-muted-foreground leading-tight">
              Library manager
            </span>
          </div>
        </Link>
      </SidebarHeader>

      <SidebarContent>
        <SidebarGroup>
          <SidebarGroupLabel>Navigation</SidebarGroupLabel>
          <SidebarGroupContent>
            <SidebarMenu>
              {navItems.map((item) => (
                <SidebarMenuItem key={item.href}>
                  <SidebarMenuButton
                    asChild
                    isActive={isNavActive(pathname, item.href)}
                    tooltip={item.label}
                  >
                    <Link href={item.href}>
                      <item.icon />
                      <span>{item.label}</span>
                    </Link>
                  </SidebarMenuButton>
                </SidebarMenuItem>
              ))}
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

      <SidebarFooter>
        <SidebarMenu>
          <SidebarMenuItem>
            <SidebarMenuButton
              asChild
              isActive={isNavActive(pathname, "/settings")}
              tooltip="Settings"
            >
              <Link href="/settings">
                <Settings />
                <span>Settings</span>
              </Link>
            </SidebarMenuButton>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarFooter>
    </Sidebar>
  )
}
