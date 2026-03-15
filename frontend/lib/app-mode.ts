const DEMO_MODE_TRUE_VALUES = new Set(["1", "true", "yes", "on"])

function parseBooleanFlag(value: string | undefined): boolean {
  if (!value) return false
  return DEMO_MODE_TRUE_VALUES.has(value.trim().toLowerCase())
}

export const isDemoMode = parseBooleanFlag(process.env.NEXT_PUBLIC_DEMO_MODE)

export const appDataMode = isDemoMode ? "demo" : "production"
