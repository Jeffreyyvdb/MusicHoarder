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
