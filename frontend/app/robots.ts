import type { MetadataRoute } from 'next'

const SITE_URL = process.env.NEXT_PUBLIC_SITE_URL ?? 'https://musichoarder.app'

const APP_ROUTES = [
  '/app',
  '/overview',
  '/review',
  '/artists',
  '/settings',
  '/spotify',
  '/api/',
]

const AI_BOTS = [
  'GPTBot',
  'ChatGPT-User',
  'OAI-SearchBot',
  'PerplexityBot',
  'Perplexity-User',
  'ClaudeBot',
  'Claude-Web',
  'anthropic-ai',
  'Google-Extended',
  'Bingbot',
  'Applebot-Extended',
]

export default function robots(): MetadataRoute.Robots {
  return {
    rules: [
      { userAgent: '*', allow: '/', disallow: APP_ROUTES },
      ...AI_BOTS.map((userAgent) => ({ userAgent, allow: '/', disallow: APP_ROUTES })),
    ],
    sitemap: `${SITE_URL}/sitemap.xml`,
    host: SITE_URL,
  }
}
