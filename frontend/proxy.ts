import { NextResponse } from 'next/server'
import type { NextRequest } from 'next/server'

export const config = {
  matcher: [
    '/app/:path*',
    '/overview/:path*',
    '/review/:path*',
    '/artists/:path*',
    '/settings/:path*',
    '/spotify/:path*',
  ],
}

export function proxy(_request: NextRequest) {
  const response = NextResponse.next()
  response.headers.set('X-Robots-Tag', 'noindex, nofollow, noarchive')
  return response
}
