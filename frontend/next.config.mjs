import path from "node:path"
import { fileURLToPath } from "node:url"

const __dirname = path.dirname(fileURLToPath(import.meta.url))

/** @type {import('next').NextConfig} */
const nextConfig = {
  output: "standalone",
  typescript: {
    ignoreBuildErrors: true,
  },
  images: {
    unoptimized: true,
  },
  turbopack: {
    root: __dirname,
  },
  async rewrites() {
    const apiUrl = process.env.MUSICHOARDER_API_URL || "https://localhost:33987"
    return [
      {
        source: "/api/:path*",
        destination: `${apiUrl}/api/:path*`,
      },
      {
        source: "/scan",
        destination: `${apiUrl}/scan`,
      },
      {
        source: "/scan/:path*",
        destination: `${apiUrl}/scan/:path*`,
      },
    ]
  },
}

export default nextConfig
