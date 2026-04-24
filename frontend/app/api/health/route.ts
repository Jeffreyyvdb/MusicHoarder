import { NextResponse } from "next/server"

// Lightweight liveness probe used by Docker/Dokploy healthchecks.
// Stays inside the Next.js runtime (no upstream fetches) so it stays green
// even when the backend API is unreachable.
export const dynamic = "force-static"

export function GET() {
  return NextResponse.json({ status: "ok" }, { status: 200 })
}
