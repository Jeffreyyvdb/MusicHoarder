/**
 * API client for MusicHoarder backend.
 * Uses /api prefix which is proxied to the backend when running under Aspire.
 */

const API_BASE = '/api';

async function fetchApi<T>(path: string, init?: RequestInit): Promise<T> {
	const res = await fetch(`${API_BASE}${path}`, {
		...init,
		headers: {
			'Content-Type': 'application/json',
			...init?.headers
		}
	});
	if (!res.ok) {
		throw new Error(`API error ${res.status}: ${res.statusText}`);
	}
	return res.json() as Promise<T>;
}

export interface Stats {
	tracks: { total: number; deleted: number };
	storage?: {
		totalBytes: number;
		totalGiB: number;
		averageBytesPerTrack: number;
	};
	duration?: {
		totalSeconds: number;
		totalHours: number;
		tracksWithDuration: number;
		averageSecondsPerTrack: number;
	};
	byExtension: { extension: string; count: number }[];
	enrichment?: {
		withFingerprint: number;
		withMusicBrainzId: number;
		withSpotifyId: number;
		withIsrc: number;
		withArtist: number;
		withAlbum: number;
		withTitle: number;
		fingerprintPct: number;
		musicBrainzPct: number;
	};
	indexWindow?: {
		oldestIndexedUtc: string | null;
		newestIndexedUtc: string | null;
		oldestFileModifiedUtc: string | null;
		newestFileModifiedUtc: string | null;
	};
}

export async function getStats(): Promise<Stats> {
	return fetchApi<Stats>('/stats');
}

export interface Song {
	id: number;
	sourcePath: string;
	fileName: string;
	extension: string;
	fileSizeBytes: number;
	lastModifiedUtc: string;
	indexedAtUtc: string;
	deletedAtUtc: string | null;
	artist: string | null;
	album: string | null;
	title: string | null;
	year: number | null;
	trackNumber: number | null;
	durationSeconds: number | null;
	isrc: string | null;
	musicBrainzId: string | null;
	spotifyId: string | null;
	enrichmentStatus: 'Pending' | 'Matched' | 'NeedsReview' | 'Failed';
	matchedBy: string | null;
	matchConfidence: number | null;
	enrichedAtUtc: string | null;
	enrichmentError: string | null;
	originalMetadataCaptured: boolean;
	originalArtist: string | null;
	originalAlbum: string | null;
	originalTitle: string | null;
	originalYear: number | null;
	originalTrackNumber: number | null;
	originalIsrc: string | null;
	originalMusicBrainzId: string | null;
	originalSpotifyId: string | null;
	originalMetadataCapturedAtUtc: string | null;
}

export interface SongsResponse {
	count: number;
	includeDeleted: boolean;
	songs: Song[];
}

export async function getSongs(includeDeleted = false): Promise<SongsResponse> {
	const params = new URLSearchParams({ includeDeleted: String(includeDeleted) });
	return fetchApi<SongsResponse>(`/songs?${params.toString()}`);
}

export interface ScanResponse {
	scanId: string;
}

export async function startScan(): Promise<ScanResponse> {
	const res = await fetch(`${API_BASE}/scan`, {
		method: 'POST'
	});
	if (!res.ok) {
		throw new Error(`API error ${res.status}: ${res.statusText}`);
	}
	return res.json() as Promise<ScanResponse>;
}
