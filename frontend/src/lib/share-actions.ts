import { toast } from 'svelte-sonner';
import { createSongShare, shareUrl } from '$lib/api-client';

/**
 * Mint (or fetch the existing) public share link for a song/album and hand it off: the system
 * share sheet (Messages/Mail/AirDrop…) when the platform offers one — that is what a Share
 * affordance promises — falling back to the clipboard everywhere else. The clipboard write gets
 * its own failure path too: it can be denied (unfocused document, missing permission) while the
 * share was still created — in that case surface the link itself instead of a misleading error.
 */
export async function createShareAndCopyLink(songId: number, scope: 'song' | 'album'): Promise<void> {
	const noun = scope === 'album' ? 'album' : 'song';
	let url: string;
	let title: string;
	try {
		const share = await createSongShare(songId, scope);
		url = shareUrl(share.token);
		title = share.artist ? `${share.title} — ${share.artist}` : share.title;
	} catch (err) {
		toast.error(err instanceof Error ? err.message : 'Could not create share link');
		return;
	}

	if (navigator.share && navigator.canShare?.({ url }) !== false) {
		try {
			await navigator.share({ url, title });
			return; // the sheet is its own confirmation — no "copied" toast to add on top
		} catch (err) {
			// AbortError is the person closing the sheet without picking anything — quit quietly
			// rather than falling back to a clipboard copy they didn't ask for.
			if (err instanceof DOMException && err.name === 'AbortError') return;
			// Any other failure (permissions policy, no user-activation) falls through to the
			// clipboard path below.
		}
	}

	try {
		await navigator.clipboard.writeText(url);
		toast.success('Share link copied', {
			description: `Anyone with the link can play this ${noun} and see its lyrics.`
		});
	} catch {
		toast.info('Share link created', { description: url, duration: 12000 });
	}
}
