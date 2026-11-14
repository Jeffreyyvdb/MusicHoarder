import { toast } from 'svelte-sonner';
import {
  acquireArtistReleases,
  chooseArtistCatalog,
  fetchArtistDiscography,
  followArtist,
  unfollowArtist,
  updateFollow,
  type ArtistDiscography,
  type ArtistRelease,
  type ArtistReleaseKind
} from '#lib/api-client.ts';
import { acquireSummary, messageOf } from '#lib/artist-discography.ts';

/**
 * One artist page's catalog side: its discography, the follow, and the "Get" requests. Shared by the
 * hero (the Follow button and its menu) and the Discography section, so a follow made in one shows in
 * the other. A page owns one instance; `load` with another artist starts over, and an answer for an
 * artist the page has since left is dropped.
 */
export class ArtistPageState {
  artist = $state<string | null>(null);
  data = $state.raw<ArtistDiscography | null>(null);
  loading = $state(false);
  error = $state<string | null>(null);
  /** Release keys with a "Get" in flight. Replaced, never mutated. */
  acquiring = $state.raw<ReadonlySet<string>>(new Set());
  followBusy = $state(false);

  #token = 0;

  get follow() {
    return this.data?.follow ?? null;
  }

  /** The catalog artist the page may follow. The server sends an id only for one it trusts. */
  get catalogArtistId(): string | null {
    return this.data?.catalog.deezerArtistId ?? null;
  }

  async load(artist: string, opts: { quiet?: boolean } = {}): Promise<void> {
    const token = ++this.#token;
    if (artist !== this.artist) {
      this.artist = artist;
      this.data = null;
    }
    if (!opts.quiet) this.loading = true;
    this.error = null;
    try {
      const data = await fetchArtistDiscography(artist);
      if (token === this.#token) this.data = data;
    } catch (err) {
      if (token === this.#token) this.error = messageOf(err, 'Could not load the discography.');
    } finally {
      if (token === this.#token) this.loading = false;
    }
  }

  #reload(): Promise<void> {
    return this.artist ? this.load(this.artist, { quiet: true }) : Promise.resolve();
  }

  async acquire(releases: readonly ArtistRelease[]): Promise<void> {
    const artist = this.artist;
    if (!artist || releases.length === 0) return;
    const keys = releases.map((r) => r.key);
    this.acquiring = new Set([...this.acquiring, ...keys]);
    try {
      const result = await acquireArtistReleases(
        artist,
        releases.map((r) => ({ key: r.key, editionIds: r.editionIds, kind: r.kind }))
      );
      const title = releases.length === 1 ? releases[0].title : `${releases.length} releases`;
      if (result.queued > 0) toast.success(title, { description: acquireSummary(result) });
      else toast(title, { description: acquireSummary(result) });
      await this.#reload();
    } catch (err) {
      toast.error(messageOf(err, 'Could not queue the download.'));
    } finally {
      this.acquiring = new Set([...this.acquiring].filter((k) => !keys.includes(k)));
    }
  }

  async choose(deezerArtistId: string): Promise<void> {
    const artist = this.artist;
    if (!artist) return;
    try {
      await chooseArtistCatalog(artist, deezerArtistId);
      await this.load(artist);
    } catch (err) {
      toast.error(messageOf(err, 'Could not save that choice.'));
    }
  }

  async toggleFollow(): Promise<void> {
    const artist = this.artist;
    const data = this.data;
    if (!artist || !data || this.followBusy) return;
    this.followBusy = true;
    try {
      if (data.follow) {
        await unfollowArtist(data.follow.id);
        this.data = { ...data, follow: null };
        toast(`Unfollowed ${data.follow.name}`);
      } else {
        const id = this.catalogArtistId;
        if (!id) return;
        const follow = await followArtist({ deezerArtistId: id, artist });
        this.data = { ...data, follow };
        toast.success(`Following ${follow.name}`, {
          description: follow.autoDownload
            ? 'New albums and EPs will download when they come out.'
            : 'You will hear about new releases.'
        });
      }
    } catch (err) {
      toast.error(messageOf(err, 'Could not change the follow.'));
    } finally {
      this.followBusy = false;
    }
  }

  async updateFollow(input: {
    releaseKinds?: ArtistReleaseKind[];
    autoDownload?: boolean;
  }): Promise<void> {
    const data = this.data;
    if (!data?.follow) return;
    try {
      const follow = await updateFollow(data.follow.id, input);
      if (this.data === data) this.data = { ...data, follow };
    } catch (err) {
      toast.error(messageOf(err, 'Could not save the follow settings.'));
    }
  }
}
