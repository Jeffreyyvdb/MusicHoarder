/**
 * Copy and data for the marketing landing page (`/`, SSR, public).
 *
 * The page shows the real app through screenshots of a real library (`static/screenshots/`, the
 * same files the README embeds), so the only data here is copy. Every fact about the system —
 * providers, ports, the pipeline, what is optional — must stay true to the README and
 * docker-compose.yml; when a feature changes there, change it here too.
 *
 * ── Design contract for every landing section (keep them visually coherent) ───────────────────
 *  • The app's own language: Apple's system palette on the app tokens, the iOS text styles, glass
 *    only on the sticky nav bar. Sections alternate `bg-background` and `bg-background-grouped`
 *    (see LandingSection); a tile on a grouped section is `bg-card`, on a plain one
 *    `bg-background-grouped dark:bg-card` (see FeatureGrid).
 *  • Tint: `text-primary` / `bg-primary` only on what you can tap (CTAs, links) — never on
 *    eyebrows, headings or decorative icons, exactly as in the app.
 *  • Width + gutters: `mx-auto max-w-[1200px] px-4 md:px-10`; anchored sections carry an `id` and
 *    `scroll-mt-14` so the sticky nav bar does not cover their heading.
 *  • Screenshots go through Screenshot.svelte (srcset, fixed aspect, lazy, light/dark pairs).
 *  • No `app.css` edits.
 */

import type { LucideIcon } from '@lucide/svelte';
import {
  Accessibility,
  AudioWaveform,
  Compass,
  Copy,
  Disc3,
  Download,
  FolderLock,
  Gift,
  Heart,
  Image,
  Layers,
  Link,
  ListVideo,
  MessageSquareQuote,
  Music2,
  Puzzle,
  Radio,
  ScrollText,
  Share2,
  Smartphone,
  Sparkles,
  Tags,
  Users,
  UsersRound,
  Vote
} from '@lucide/svelte';

export const githubUrl = 'https://github.com/Jeffreyyvdb/MusicHoarder';

export type Feature = { icon: LucideIcon; title: string; body: string };

/* ── Listen ──────────────────────────────────────────────────────────────────────────────────── */

export const listenFeatures: readonly Feature[] = [
  {
    icon: MessageSquareQuote,
    title: 'Now Playing, with the words',
    body: 'Full-screen and always dark, over the cover or the song’s own music video. Synced lyrics follow along, and tapping a line jumps there.'
  },
  {
    icon: Smartphone,
    title: 'Built like an iPhone app',
    body: 'A floating tab bar, large titles that collapse as you scroll, grouped lists, sheets, long-press menus and an edge swipe for Back.'
  },
  {
    icon: Radio,
    title: 'It keeps playing',
    body: 'When the queue runs out, a radio continues with similar songs from your library. It plays on in the background, and decodes formats Safari can’t play.'
  },
  {
    icon: Accessibility,
    title: 'Accessible by default',
    body: 'Text follows the iPhone’s Text Size, and Reduce Motion, Reduce Transparency and Increase Contrast are all honoured. Every text colour is contrast-checked.'
  },
  {
    icon: Heart,
    title: 'Likes and play history',
    body: 'Heart anything. Plays are counted, so the Overview’s shelves come from what you actually listen to: favourites, recently added, most played.'
  },
  {
    icon: Download,
    title: 'Install it',
    body: 'Add it to your iPhone’s Home Screen, or install it from Chrome or Edge. A native Android app mirrors the same design and behaviour.'
  }
];

/* ── Organize ────────────────────────────────────────────────────────────────────────────────── */

export const organizeFeatures: readonly Feature[] = [
  {
    icon: AudioWaveform,
    title: 'Identified by sound',
    body: 'Chromaprint and AcoustID fingerprint every file, so track_047.mp3 with empty tags is recognised for what it is.'
  },
  {
    icon: Vote,
    title: 'Consensus, not the first hit',
    body: 'AcoustID, MusicBrainz, Spotify, Apple Music, Deezer and community trackers each answer independently. Agreement auto-matches; disagreement goes to you.'
  },
  {
    icon: FolderLock,
    title: 'Your originals, untouched',
    body: 'The source is read-only. Clean copies are written to a separate library, and a good tag is never overwritten without a strong consensus.'
  },
  {
    icon: Disc3,
    title: 'Whole, complete albums',
    body: 'One identity per album folder keeps albums whole in Navidrome and friends, and a cross-checked tracklist shows which tracks you’re missing.'
  },
  {
    icon: Image,
    title: 'Real covers, synced lyrics',
    body: 'Artwork from the folder, the file or Cover Art Archive, Deezer and iTunes. Time-synced lyrics from LRCLIB, embedded in the file.'
  },
  {
    icon: Copy,
    title: 'The best copy wins',
    body: 'Duplicates are grouped by fingerprint and ranked by codec, bitrate and tag quality, so only the best version is built.'
  }
];

/* ── Review ──────────────────────────────────────────────────────────────────────────────────── */

export const reviewFeatures: readonly Feature[] = [
  {
    icon: Tags,
    title: 'Tag review',
    body: 'Low-confidence matches with every provider’s candidate and a field-by-field diff of what will be written. Approve, correct, or bulk-approve.'
  },
  {
    icon: Layers,
    title: 'Duplicates and names',
    body: 'Compare fingerprint twins and keep the best, and merge spellings of one artist or album into a single name.'
  },
  {
    icon: Sparkles,
    title: 'Graded by an LLM',
    body: 'An optional quality grader scores every match 0–100 and flags what looks wrong. It is OpenAI-compatible, and works with a local Ollama.'
  },
  {
    icon: ScrollText,
    title: 'Nothing is a black box',
    body: 'Any track tells its whole story: the file it came from, every provider’s answer, its AI grade, and a timeline of how it reached the library.'
  }
];

/* ── Grow ────────────────────────────────────────────────────────────────────────────────────── */

export const growFeatures: readonly Feature[] = [
  {
    icon: Compass,
    title: 'Discover',
    body: 'Browse editorial and chart playlists by genre, or paste a playlist link, and subscribe so new tracks are wishlisted as they appear.'
  },
  {
    icon: Music2,
    title: 'Spotify sync',
    body: 'Connect Spotify read-only to see your Liked Songs and playlists matched against your library, and sync any of them into the wishlist.'
  },
  {
    icon: Gift,
    title: 'Wishlist, downloaded',
    body: 'Optionally, wishlisted songs are fetched for you through a chain of sources — a self-run Soulseek client and yt-dlp among them — and land in the right album.'
  },
  {
    icon: Link,
    title: 'Add from a link',
    body: 'Paste a Spotify track or YouTube link, and the song is downloaded straight into your library.'
  },
  {
    icon: ListVideo,
    title: 'Playlist sync',
    body: 'Mirror Liked Songs or any playlist as an .m3u8 file in the library, so Navidrome, Plex and Jellyfin pick it up.'
  },
  {
    icon: Puzzle,
    title: 'And more',
    body: 'Two-way likes with Navidrome, in-place quality upgrades from Soulseek, and NAS-to-VPS instance sync — each optional.'
  }
];

/* ── Share ───────────────────────────────────────────────────────────────────────────────────── */

export const shareFeatures: readonly Feature[] = [
  {
    icon: Share2,
    title: 'Share links',
    body: 'A revocable, no-account link to one track or a whole album, played on a clean page with full-screen synced lyrics.'
  },
  {
    icon: Users,
    title: 'Member accounts',
    body: 'Invite people by email and share albums, artists or everything. They get the same listening app, with their own likes and plays.'
  },
  {
    icon: UsersRound,
    title: 'More than one account',
    body: 'Sign in to several accounts on one browser or phone, and switch between them from the account menu.'
  }
];

/* ── Self-host: the real homelab flow (root README.md + docker-compose.yml + .env.example) ───── */

/** The exact homelab quickstart, copy-paste ready. Pulls prebuilt GHCR images — no checkout, no build. */
export const installCommand = `curl -fsSLO https://raw.githubusercontent.com/Jeffreyyvdb/MusicHoarder/main/docker-compose.yml
curl -fsSL https://raw.githubusercontent.com/Jeffreyyvdb/MusicHoarder/main/.env.example -o .env
# edit .env — set your two folders, a Postgres password, owner email + public URL
docker compose up -d`;

/** Abridged-but-accurate excerpt of the repo's docker-compose.yml (real images, ports, mounts). */
export const composeSnippet = `services:
  postgres:                       # metadata + cache
    image: postgres:17

  musichoarder:                   # API + the whole pipeline
    image: ghcr.io/jeffreyyvdb/musichoarder/api:latest
    ports: ["5050:8080"]
    volumes:
      - \${MUSIC_SOURCE_PATH}:/music/source:ro        # read-only
      - \${MUSIC_DESTINATION_PATH}:/music/destination # clean library
    environment:
      - MusicEnricher__SourceDirectory=/music/source
      - MusicEnricher__DestinationDirectory=/music/destination
      - MusicEnricher__AcoustIdApiKey=\${ACOUSTID_API_KEY:-}
    depends_on: [postgres]

  frontend:                       # the web UI you're looking at
    image: ghcr.io/jeffreyyvdb/musichoarder/frontend:latest
    ports: ["3000:3000"]
    depends_on: [musichoarder]`;

/** Three-step quickstart, derived from the README self-hosting section. */
export const quickstartSteps: ReadonlyArray<{ title: string; body: string }> = [
  {
    title: 'Download and configure',
    body: 'Grab docker-compose.yml and .env.example — no repo clone needed. Point MUSIC_SOURCE_PATH at your messy folder and MUSIC_DESTINATION_PATH at an empty one for the clean library, then set a POSTGRES_PASSWORD, OWNER_EMAIL and PUBLIC_BASE_URL.'
  },
  {
    title: 'Bring it up',
    body: 'Run docker compose up -d. It pulls the prebuilt API and frontend images from GHCR, starts PostgreSQL, applies migrations, and the scanner begins walking your source folder straight away.'
  },
  {
    title: 'Open it',
    body: 'Visit the frontend on :3000 (the API is on :5050), watch the Pipeline fill, and clear anything that lands in your Inbox. An AcoustID key is optional but makes matching far more confident.'
  }
];
