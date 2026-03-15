import type { NextRequest } from "next/server"

const LOCAL_API_FALLBACK = "http://localhost:5107"

const API_URL_CANDIDATE_KEYS = [
  "MUSICHOARDER_API_URL",
  "services__musichoarder-api__http__0",
  "services__musichoarder-api__http",
  "SERVICES__MUSICHOARDER-API__HTTP__0",
  "SERVICES__MUSICHOARDER-API__HTTP",
  "ConnectionStrings__musichoarder-api",
  "CONNECTIONSTRINGS__MUSICHOARDER-API",
]

function getApiBaseUrl(): string {
  for (const key of API_URL_CANDIDATE_KEYS) {
    const value = process.env[key]
    if (value) {
      return value
    }
  }

  const discoveredKey = Object.keys(process.env).find((key) =>
    key.toLowerCase().startsWith("services__musichoarder-api__http")
  )
  if (discoveredKey) {
    const discoveredValue = process.env[discoveredKey]
    if (discoveredValue) {
      return discoveredValue
    }
  }

  return LOCAL_API_FALLBACK
}

function getTargetUrl(request: NextRequest, pathSegments: string[]): string {
  const baseUrl = getApiBaseUrl().replace(/\/$/, "")
  const apiPath = pathSegments.join("/")
  const queryString = request.nextUrl.search
  return `${baseUrl}/${apiPath}${queryString}`
}

async function proxyRequest(
  request: NextRequest,
  context: { params: Promise<{ path: string[] }> }
): Promise<Response> {
  const { path } = await context.params
  const targetUrl = getTargetUrl(request, path)
  const method = request.method.toUpperCase()
  const shouldForwardBody = method !== "GET" && method !== "HEAD"

  const requestHeaders = new Headers(request.headers)
  requestHeaders.delete("host")

  const response = await fetch(targetUrl, {
    method,
    headers: requestHeaders,
    body: shouldForwardBody ? await request.arrayBuffer() : undefined,
    cache: "no-store",
    redirect: "follow",
  })

  const responseHeaders = new Headers(response.headers)
  responseHeaders.delete("content-encoding")
  responseHeaders.delete("content-length")

  return new Response(response.body, {
    status: response.status,
    statusText: response.statusText,
    headers: responseHeaders,
  })
}

export async function GET(
  request: NextRequest,
  context: { params: Promise<{ path: string[] }> }
): Promise<Response> {
  return proxyRequest(request, context)
}

export async function POST(
  request: NextRequest,
  context: { params: Promise<{ path: string[] }> }
): Promise<Response> {
  return proxyRequest(request, context)
}

export async function PUT(
  request: NextRequest,
  context: { params: Promise<{ path: string[] }> }
): Promise<Response> {
  return proxyRequest(request, context)
}

export async function PATCH(
  request: NextRequest,
  context: { params: Promise<{ path: string[] }> }
): Promise<Response> {
  return proxyRequest(request, context)
}

export async function DELETE(
  request: NextRequest,
  context: { params: Promise<{ path: string[] }> }
): Promise<Response> {
  return proxyRequest(request, context)
}
