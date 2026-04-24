"use client"

import * as React from "react"
import { Check, Monitor, Moon, Sun } from "lucide-react"
import { useTheme } from "next-themes"
import { Button } from "@/components/ui/button"
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import { cn } from "@/lib/utils"

type ThemeToggleVariant = "outline" | "ghost"

type ThemeToggleProps = {
  className?: string
  variant?: ThemeToggleVariant
}

const THEME_OPTIONS = [
  { value: "light", label: "Light", icon: Sun },
  { value: "dark", label: "Dark", icon: Moon },
  { value: "system", label: "System", icon: Monitor },
] as const

export function ThemeToggle({ className, variant = "ghost" }: ThemeToggleProps) {
  const { theme, setTheme, resolvedTheme } = useTheme()
  const [mounted, setMounted] = React.useState(false)

  React.useEffect(() => {
    setMounted(true)
  }, [])

  const sizeClasses = variant === "ghost" ? "size-9" : "size-9 shrink-0"
  const outlineClasses =
    variant === "outline"
      ? "border-border bg-background/80 backdrop-blur-sm shadow-sm hover:bg-accent"
      : ""

  if (!mounted) {
    return (
      <Button
        variant={variant}
        size="icon"
        className={cn(sizeClasses, outlineClasses, className)}
        disabled
        aria-hidden
        tabIndex={-1}
      >
        <Sun className="size-[1.125rem] opacity-0" />
      </Button>
    )
  }

  const isDark = resolvedTheme === "dark"
  const active = theme ?? "system"

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          type="button"
          variant={variant}
          size="icon"
          className={cn(sizeClasses, outlineClasses, "transition-colors", className)}
          aria-label="Change theme"
          title="Change theme"
        >
          {isDark ? (
            <Moon className="size-[1.125rem] text-foreground" />
          ) : (
            <Sun className="size-[1.125rem] text-foreground" />
          )}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="min-w-36">
        {THEME_OPTIONS.map(({ value, label, icon: Icon }) => {
          const selected = active === value
          return (
            <DropdownMenuItem
              key={value}
              onSelect={() => setTheme(value)}
              className="justify-between"
            >
              <span className="flex items-center gap-2">
                <Icon className="size-4" />
                {label}
              </span>
              {selected ? (
                <Check className="size-4 text-muted-foreground" />
              ) : null}
            </DropdownMenuItem>
          )
        })}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
