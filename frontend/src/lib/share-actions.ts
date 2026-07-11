import { toast } from 'svelte-sonner';
import { createSongShare, shareUrl } from '$lib/api-client';

/**
 * Mint (or fetch the existing) public share link for a song/album and copy it to the
 * clipboard. The clipboard write gets its own failure path: it can be denied (unfocused
 * document, missing permission) while the share was still created — in that case surface
 * the link itself instead of a misleading error.
 */
export async function createShareAndCopyLink(songId: number, scope: 'song' | 'album'): Promise<void> {
	const noun = scope === 'album' ? 'album' : 'song';
	let url: string;
	try {
		const share = await createSongShare(songId, scope);
		url = shareUrl(share.token);
	} catch (err) {
		toast.error(err instanceof Error ? err.message : 'Could not create share link');
		return;
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
