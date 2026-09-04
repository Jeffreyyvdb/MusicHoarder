using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Tests.Scanner;

public class DuplicateDetectionServiceTests
{
    [Fact]
    public async Task DetectDuplicates_FlagsLowerQualityVersions_KeepsBest()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track.flac", ".flac", "FP_A", bitrate: null, size: 50_000_000),
            CreateSong(2, "/b/track.mp3", ".mp3", "FP_A", bitrate: 320, size: 10_000_000),
            CreateSong(3, "/c/track.mp3", ".mp3", "FP_A", bitrate: 128, size: 4_000_000));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(1, result.GroupsFound);
        Assert.Equal(2, result.DuplicatesFlagged);
        Assert.False(songs[0].IsDuplicate);
        Assert.Null(songs[0].DuplicateOfId);
        Assert.True(songs[1].IsDuplicate);
        Assert.Equal(1, songs[1].DuplicateOfId);
        Assert.True(songs[2].IsDuplicate);
        Assert.Equal(1, songs[2].DuplicateOfId);
    }

    [Fact]
    public async Task DetectDuplicates_PrefersMp3HighBitrate_OverLowBitrate()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track128.mp3", ".mp3", "FP_B", bitrate: 128, size: 4_000_000),
            CreateSong(2, "/b/track320.mp3", ".mp3", "FP_B", bitrate: 320, size: 10_000_000));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.DetectDuplicatesAsync();

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.True(songs[0].IsDuplicate);
        Assert.Equal(2, songs[0].DuplicateOfId);
        Assert.False(songs[1].IsDuplicate);
        Assert.Null(songs[1].DuplicateOfId);
    }

    [Fact]
    public async Task DetectDuplicates_PrefersMatchedCopy_OverBiggerUnmatchedTwin()
    {
        // Identical audio, equal format: one copy is enriched-Matched with verified tags, the other is
        // a bigger file that never matched (e.g. mislabeled — same recording under a wrong title).
        // Electing the unmatched one as "best" would knock the correctly-tagged copy out of the build.
        await using var db = CreateDbContext();
        var matched = CreateSong(1, "/a/survival tactics.flac", ".flac", "FP_M", bitrate: null, size: 22_000_000);
        matched.EnrichmentStatus = EnrichmentStatus.Matched;
        var mislabeled = CreateSong(2, "/b/survivors guilt.flac", ".flac", "FP_M", bitrate: null, size: 23_000_000);
        db.Songs.AddRange(matched, mislabeled);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.DetectDuplicatesAsync();

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.False(songs[0].IsDuplicate);
        Assert.True(songs[1].IsDuplicate);
        Assert.Equal(1, songs[1].DuplicateOfId);
    }

    [Fact]
    public async Task DetectDuplicates_PrefersBuiltCopy_OverBiggerUnbuiltTwin()
    {
        // At equal quality and enrichment standing, flagging the already-built copy would orphan its
        // destination file and rebuild the same audio under a new name — keep the built one as best.
        await using var db = CreateDbContext();
        var unbuilt = CreateSong(1, "/a/track.flac", ".flac", "FP_N", bitrate: null, size: 23_000_000);
        unbuilt.EnrichmentStatus = EnrichmentStatus.Matched;
        var built = CreateSong(2, "/b/track.flac", ".flac", "FP_N", bitrate: null, size: 22_000_000);
        built.EnrichmentStatus = EnrichmentStatus.Matched;
        built.MarkBuildDone("/dest/Artist/Album/01 - Track.flac");
        db.Songs.AddRange(unbuilt, built);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.DetectDuplicatesAsync();

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.True(songs[0].IsDuplicate);
        Assert.Equal(2, songs[0].DuplicateOfId);
        Assert.False(songs[1].IsDuplicate);
    }

    [Fact]
    public async Task DetectDuplicates_NoGroupsWithSingleTrack()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track1.flac", ".flac", "FP_C", bitrate: null, size: 50_000_000, title: "Track One"),
            CreateSong(2, "/b/track2.mp3", ".mp3", "FP_D", bitrate: 320, size: 10_000_000, title: "Track Two"));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(0, result.GroupsFound);
        Assert.Equal(0, result.DuplicatesFlagged);

        var songs = await db.Songs.ToListAsync();
        Assert.All(songs, s => Assert.False(s.IsDuplicate));
    }

    [Fact]
    public async Task DetectDuplicates_IgnoresDeletedSongs()
    {
        await using var db = CreateDbContext();
        var song1 = CreateSong(1, "/a/track.flac", ".flac", "FP_E", bitrate: null, size: 50_000_000);
        var song2 = CreateSong(2, "/b/track.mp3", ".mp3", "FP_E", bitrate: 320, size: 10_000_000);
        song2.SoftDelete();
        db.Songs.AddRange(song1, song2);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(0, result.GroupsFound);
        Assert.False(song1.IsDuplicate);
    }

    [Fact]
    public async Task DetectDuplicates_IgnoresNullFingerprints()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track1.mp3", ".mp3", null, bitrate: 320, size: 10_000_000),
            CreateSong(2, "/b/track2.mp3", ".mp3", null, bitrate: 128, size: 4_000_000));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(0, result.GroupsFound);
    }

    [Fact]
    public async Task DetectDuplicates_IgnoresEmptyFingerprints()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track1.mp3", ".mp3", "", bitrate: 320, size: 10_000_000),
            CreateSong(2, "/b/track2.mp3", ".mp3", "", bitrate: 128, size: 4_000_000));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(0, result.GroupsFound);
    }

    [Fact]
    public async Task DetectDuplicates_ClearsPreviousDuplicateFlags_WhenSourceFileRemoved()
    {
        await using var db = CreateDbContext();
        var song1 = CreateSong(1, "/a/track.flac", ".flac", "FP_F", bitrate: null, size: 50_000_000);
        var song2 = CreateSong(2, "/b/track.mp3", ".mp3", "FP_F", bitrate: 320, size: 10_000_000);
        song2.MarkAsDuplicate(1);
        db.Songs.AddRange(song1, song2);
        await db.SaveChangesAsync();

        song1.Fingerprint = "FP_NEW";
        song1.Title = "Different Track";
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(0, result.GroupsFound);
        Assert.Equal(1, result.DuplicatesCleared);
        Assert.False(songs[0].IsDuplicate);
        Assert.False(songs[1].IsDuplicate);
        Assert.Null(songs[1].DuplicateOfId);
    }

    [Fact]
    public async Task DetectDuplicates_TiesBreakByFileSize_ThenById()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track_small.mp3", ".mp3", "FP_G", bitrate: 320, size: 9_000_000),
            CreateSong(2, "/b/track_large.mp3", ".mp3", "FP_G", bitrate: 320, size: 10_000_000));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.DetectDuplicatesAsync();

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.True(songs[0].IsDuplicate);
        Assert.Equal(2, songs[0].DuplicateOfId);
        Assert.False(songs[1].IsDuplicate);
    }

    [Fact]
    public async Task DetectDuplicates_IdempotentOnRerun()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track.flac", ".flac", "FP_H", bitrate: null, size: 50_000_000),
            CreateSong(2, "/b/track.mp3", ".mp3", "FP_H", bitrate: 320, size: 10_000_000));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result1 = await service.DetectDuplicatesAsync();
        Assert.Equal(1, result1.DuplicatesFlagged);

        var result2 = await service.DetectDuplicatesAsync();
        Assert.Equal(0, result2.DuplicatesFlagged);
        Assert.Equal(0, result2.DuplicatesCleared);

        // Idempotent on the links table too: still exactly one row for the pair.
        Assert.Equal(1, await db.SongDuplicateLinks.CountAsync());
    }

    [Fact]
    public async Task DetectDuplicates_HandlesMultipleGroups()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track1.flac", ".flac", "FP_I", bitrate: null, size: 50_000_000, title: "Track One"),
            CreateSong(2, "/b/track1.mp3", ".mp3", "FP_I", bitrate: 320, size: 10_000_000, title: "Track One"),
            CreateSong(3, "/c/track2.flac", ".flac", "FP_J", bitrate: null, size: 40_000_000, title: "Track Two"),
            CreateSong(4, "/d/track2.mp3", ".mp3", "FP_J", bitrate: 128, size: 4_000_000, title: "Track Two"),
            CreateSong(5, "/e/unique.flac", ".flac", "FP_K", bitrate: null, size: 30_000_000, title: "Unique"));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(2, result.GroupsFound);
        Assert.Equal(2, result.DuplicatesFlagged);

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.False(songs[0].IsDuplicate); // FP_I best (FLAC)
        Assert.True(songs[1].IsDuplicate);  // FP_I dup (MP3 320)
        Assert.False(songs[2].IsDuplicate); // FP_J best (FLAC)
        Assert.True(songs[3].IsDuplicate);  // FP_J dup (MP3 128)
        Assert.False(songs[4].IsDuplicate); // unique
    }

    [Fact]
    public async Task DetectDuplicates_PrefersFlacOverAllMp3Bitrates()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track.mp3", ".mp3", "FP_L", bitrate: 320, size: 10_000_000),
            CreateSong(2, "/b/track.flac", ".flac", "FP_L", bitrate: null, size: 50_000_000));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.DetectDuplicatesAsync();

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.True(songs[0].IsDuplicate);
        Assert.Equal(2, songs[0].DuplicateOfId);
        Assert.False(songs[1].IsDuplicate);
    }

    [Fact]
    public void QualityScore_OrdersFlacAboveWavAboveLossyAboveUnknown()
    {
        var flac = CreateSong(1, "/a/track.flac", ".flac", "FP", bitrate: null, size: 50_000_000);
        var wav = CreateSong(2, "/a/track.wav", ".wav", "FP", bitrate: null, size: 100_000_000);
        var mp3High = CreateSong(3, "/a/track.mp3", ".mp3", "FP", bitrate: 320, size: 10_000_000);
        var mp3Low = CreateSong(4, "/b/track.mp3", ".mp3", "FP", bitrate: 128, size: 4_000_000);
        var mp3NoBitrate = CreateSong(5, "/c/track.mp3", ".mp3", "FP", bitrate: null, size: 4_000_000);
        var unknown = CreateSong(6, "/a/track.xyz", ".xyz", "FP", bitrate: null, size: 10_000_000);

        Assert.True(IDuplicateDetectionService.QualityScore(flac) > IDuplicateDetectionService.QualityScore(wav));
        Assert.True(IDuplicateDetectionService.QualityScore(wav) > IDuplicateDetectionService.QualityScore(mp3High));
        Assert.True(IDuplicateDetectionService.QualityScore(mp3High) > IDuplicateDetectionService.QualityScore(mp3Low));
        Assert.True(IDuplicateDetectionService.QualityScore(mp3Low) > IDuplicateDetectionService.QualityScore(mp3NoBitrate));
        Assert.True(IDuplicateDetectionService.QualityScore(mp3NoBitrate) > IDuplicateDetectionService.QualityScore(unknown));
    }

    [Fact]
    public async Task DetectDuplicates_LibraryBuilderSkipsDuplicates()
    {
        await using var db = CreateDbContext();
        var flac = CreateSong(1, "/a/track.flac", ".flac", "FP_M", bitrate: null, size: 50_000_000);
        flac.EnrichmentStatus = EnrichmentStatus.Matched;
        flac.LyricsStatus = LyricsStatus.Fetched;
        var mp3 = CreateSong(2, "/b/track.mp3", ".mp3", "FP_M", bitrate: 320, size: 10_000_000);
        mp3.EnrichmentStatus = EnrichmentStatus.Matched;
        mp3.LyricsStatus = LyricsStatus.Fetched;
        db.Songs.AddRange(flac, mp3);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.DetectDuplicatesAsync();

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();

        Assert.True(songs[0].IsReadyForBuild);
        Assert.False(songs[1].IsReadyForBuild);
    }

    [Fact]
    public async Task DetectDuplicates_IgnoresDemoSongs()
    {
        // The demo tenant's real-file rows (IsSynthetic == false) must stay out of duplicate
        // detection entirely — a shared fingerprint must not flag either side.
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track.flac", ".flac", "FP_N", bitrate: null, size: 50_000_000),
            CreateSong(2, "/demo/track.mp3", ".mp3", "FP_N", bitrate: 320, size: 10_000_000,
                owner: MusicHoarder.Api.Auth.WellKnownUsers.DemoId));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(0, result.GroupsFound);
        var songs = await db.Songs.IgnoreQueryFilters().ToListAsync();
        Assert.All(songs, s => Assert.False(s.IsDuplicate));
    }

    // --- Detection v2: per-owner scoping, similarity confirmation, links table ---

    [Fact]
    public async Task DetectDuplicates_NeverGroupsAcrossOwners()
    {
        // Same fingerprint under two different (real) owners: never linked, never flagged.
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track.flac", ".flac", "FP_X", bitrate: null, size: 50_000_000),
            CreateSong(2, "/b/track.mp3", ".mp3", "FP_X", bitrate: 320, size: 10_000_000, owner: Guid.NewGuid()));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(0, result.GroupsFound);
        Assert.Equal(0, await db.SongDuplicateLinks.CountAsync());
        var songs = await db.Songs.IgnoreQueryFilters().ToListAsync();
        Assert.All(songs, s => Assert.False(s.IsDuplicate));
    }

    [Fact]
    public async Task DetectDuplicates_MetadataBlock_ConfirmedByFingerprintSimilarity()
    {
        // Two encodings of one recording: fingerprints differ as strings but decode to similar
        // frames — this is the FLAC-vs-MP3 case the old exact-match detection missed.
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track.flac", ".flac", "ENC_A", bitrate: null, size: 50_000_000, durationSeconds: 200),
            CreateSong(2, "/b/track.mp3", ".mp3", "ENC_B", bitrate: 320, size: 10_000_000, durationSeconds: 201));
        await db.SaveChangesAsync();

        var service = CreateService(db, gate: new FakeGate(["ENC_A", "ENC_B"], similarity: 0.95));
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(1, result.GroupsFound);
        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.False(songs[0].IsDuplicate);
        Assert.True(songs[1].IsDuplicate);
        Assert.Equal(1, songs[1].DuplicateOfId);

        var link = Assert.Single(await db.SongDuplicateLinks.ToListAsync());
        Assert.Equal(DuplicateConfidence.Confirmed, link.Confidence);
        Assert.True(link.Reasons.HasFlag(DuplicateMatchReason.Metadata));
        Assert.True(link.Reasons.HasFlag(DuplicateMatchReason.FingerprintSimilarity));
        Assert.Equal(0.95, link.Similarity!.Value, precision: 3);
    }

    [Fact]
    public async Task DetectDuplicates_MetadataOnly_MissingFingerprints_IsSuspectedNotFlagged()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track.mp3", ".mp3", null, bitrate: 320, size: 10_000_000, durationSeconds: 200),
            CreateSong(2, "/b/track.mp3", ".mp3", null, bitrate: 128, size: 4_000_000, durationSeconds: 200));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        // Surfaced for review, but IsDuplicate is untouched so build behavior can't change on a guess.
        Assert.Equal(0, result.GroupsFound);
        Assert.Equal(1, result.SuspectedPairs);
        var songs = await db.Songs.ToListAsync();
        Assert.All(songs, s => Assert.False(s.IsDuplicate));

        var link = Assert.Single(await db.SongDuplicateLinks.ToListAsync());
        Assert.Equal(DuplicateConfidence.Suspected, link.Confidence);
        Assert.Equal(DuplicateMatchReason.Metadata, link.Reasons);
        Assert.Null(link.Similarity);
    }

    [Fact]
    public async Task DetectDuplicates_StrongQualifier_PreventsLiveVsStudioPairing()
    {
        // NormalizeForSearch strips "(Live)" so both titles share a metadata block key — the
        // strong-qualifier gate must still keep them apart.
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track.mp3", ".mp3", null, bitrate: 320, size: 10_000_000, title: "My Song", durationSeconds: 200),
            CreateSong(2, "/b/track live.mp3", ".mp3", null, bitrate: 320, size: 10_000_000, title: "My Song (Live)", durationSeconds: 200));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(0, result.SuspectedPairs);
        Assert.Equal(0, await db.SongDuplicateLinks.CountAsync());
    }

    [Fact]
    public async Task DetectDuplicates_LowSimilarity_DropsPairEntirely()
    {
        // Decodable fingerprints that strongly disagree are proof of different recordings — the
        // pair isn't even surfaced as suspected, despite identical metadata.
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track.flac", ".flac", "ENC_A", bitrate: null, size: 50_000_000, durationSeconds: 200),
            CreateSong(2, "/b/track.mp3", ".mp3", "ENC_B", bitrate: 320, size: 10_000_000, durationSeconds: 200));
        await db.SaveChangesAsync();

        var service = CreateService(db, gate: new FakeGate(["ENC_A", "ENC_B"], similarity: 0.30));
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(0, result.GroupsFound);
        Assert.Equal(0, result.SuspectedPairs);
        Assert.Equal(0, await db.SongDuplicateLinks.CountAsync());
    }

    [Fact]
    public async Task DetectDuplicates_MidSimilarity_IsSuspected()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track.flac", ".flac", "ENC_A", bitrate: null, size: 50_000_000, durationSeconds: 200),
            CreateSong(2, "/b/track.mp3", ".mp3", "ENC_B", bitrate: 320, size: 10_000_000, durationSeconds: 200));
        await db.SaveChangesAsync();

        var service = CreateService(db, gate: new FakeGate(["ENC_A", "ENC_B"], similarity: 0.70));
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(0, result.GroupsFound);
        Assert.Equal(1, result.SuspectedPairs);
        var link = Assert.Single(await db.SongDuplicateLinks.ToListAsync());
        Assert.Equal(DuplicateConfidence.Suspected, link.Confidence);
        Assert.Equal(0.70, link.Similarity!.Value, precision: 3);
    }

    [Fact]
    public async Task DetectDuplicates_DurationMismatch_PreventsMetadataPairing()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track.mp3", ".mp3", null, bitrate: 320, size: 10_000_000, durationSeconds: 200),
            CreateSong(2, "/b/track.mp3", ".mp3", null, bitrate: 128, size: 4_000_000, durationSeconds: 210));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(0, result.SuspectedPairs);
        Assert.Equal(0, await db.SongDuplicateLinks.CountAsync());
    }

    [Fact]
    public async Task DetectDuplicates_DismissedLink_SurvivesRerun_AndSuppressesFlagging()
    {
        await using var db = CreateDbContext();
        var song1 = CreateSong(1, "/a/track.flac", ".flac", "FP_D1", bitrate: null, size: 50_000_000);
        var song2 = CreateSong(2, "/b/track.mp3", ".mp3", "FP_D1", bitrate: 320, size: 10_000_000);
        db.Songs.AddRange(song1, song2);
        db.SongDuplicateLinks.Add(new SongDuplicateLink
        {
            OwnerUserId = song1.OwnerUserId,
            SongIdLow = 1,
            SongIdHigh = 2,
            Status = DuplicateLinkStatus.Dismissed,
            Confidence = DuplicateConfidence.Confirmed,
            DetectedAtUtc = DateTime.UtcNow,
            DismissedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        // Exact-fingerprint evidence exists, but the user said "not duplicates" — respected forever.
        Assert.Equal(0, result.GroupsFound);
        Assert.Equal(0, result.DuplicatesFlagged);
        var link = Assert.Single(await db.SongDuplicateLinks.ToListAsync());
        Assert.Equal(DuplicateLinkStatus.Dismissed, link.Status);
        var songs = await db.Songs.ToListAsync();
        Assert.All(songs, s => Assert.False(s.IsDuplicate));
    }

    [Fact]
    public async Task DetectDuplicates_PinnedKeeper_OverridesQualityElection()
    {
        await using var db = CreateDbContext();
        var flac = CreateSong(1, "/a/track.flac", ".flac", "FP_P1", bitrate: null, size: 50_000_000);
        var mp3 = CreateSong(2, "/b/track.mp3", ".mp3", "FP_P1", bitrate: 320, size: 10_000_000);
        mp3.DuplicateKeeperPinnedAtUtc = DateTime.UtcNow;
        db.Songs.AddRange(flac, mp3);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.DetectDuplicatesAsync();

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.True(songs[0].IsDuplicate);
        Assert.Equal(2, songs[0].DuplicateOfId);
        Assert.False(songs[1].IsDuplicate);
    }

    [Fact]
    public async Task DetectDuplicates_SharedAcoustId_PairsDespiteDifferentMetadata()
    {
        // A mistagged copy (different title/artist) still pairs via the shared AcoustID track id and
        // is confirmed by fingerprint similarity.
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/proper.flac", ".flac", "ENC_A", bitrate: null, size: 50_000_000,
                title: "Proper Title", artist: "Proper Artist", acoustIdTrackId: "acoustid-123"),
            CreateSong(2, "/b/mistagged.mp3", ".mp3", "ENC_B", bitrate: 320, size: 10_000_000,
                title: "Wrong Title", artist: "Wrong Artist", acoustIdTrackId: "acoustid-123"));
        await db.SaveChangesAsync();

        var service = CreateService(db, gate: new FakeGate(["ENC_A", "ENC_B"], similarity: 0.92));
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(1, result.GroupsFound);
        var link = Assert.Single(await db.SongDuplicateLinks.ToListAsync());
        Assert.True(link.Reasons.HasFlag(DuplicateMatchReason.AcoustIdTrack));
        Assert.True(link.Reasons.HasFlag(DuplicateMatchReason.FingerprintSimilarity));
    }

    [Fact]
    public async Task DetectDuplicates_SharedIsrc_WithoutFingerprints_IsSuspectedOnly()
    {
        // ISRC alone never auto-confirms — dirty tags share ISRCs.
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/a.mp3", ".mp3", null, bitrate: 320, size: 10_000_000,
                title: "Title A", artist: "Artist A", isrc: "USABC1234567"),
            CreateSong(2, "/b/b.mp3", ".mp3", null, bitrate: 128, size: 4_000_000,
                title: "Title B", artist: "Artist B", isrc: "us-abc-12-34567"));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(0, result.GroupsFound);
        Assert.Equal(1, result.SuspectedPairs);
        var link = Assert.Single(await db.SongDuplicateLinks.ToListAsync());
        Assert.Equal(DuplicateMatchReason.Isrc, link.Reasons);
        Assert.Equal(DuplicateConfidence.Suspected, link.Confidence);
        var songs = await db.Songs.ToListAsync();
        Assert.All(songs, s => Assert.False(s.IsDuplicate));
    }

    [Fact]
    public async Task DetectDuplicates_StaleActiveLink_IsRemoved()
    {
        await using var db = CreateDbContext();
        var song1 = CreateSong(1, "/a/one.mp3", ".mp3", "FP_S1", bitrate: 320, size: 10_000_000, title: "One");
        var song2 = CreateSong(2, "/b/two.mp3", ".mp3", "FP_S2", bitrate: 320, size: 10_000_000, title: "Two");
        db.Songs.AddRange(song1, song2);
        db.SongDuplicateLinks.Add(new SongDuplicateLink
        {
            OwnerUserId = song1.OwnerUserId,
            SongIdLow = 1,
            SongIdHigh = 2,
            Status = DuplicateLinkStatus.Active,
            Confidence = DuplicateConfidence.Confirmed,
            Reasons = DuplicateMatchReason.ExactFingerprint,
            DetectedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.DetectDuplicatesAsync();

        Assert.Equal(0, await db.SongDuplicateLinks.CountAsync());
    }

    [Fact]
    public async Task DetectDuplicates_ExactFingerprint_ConfirmsWithoutAudioComparison()
    {
        // Byte-identical compressed fingerprints are the strongest evidence there is: the pair is
        // Confirmed at similarity 1.0 even when a decoded comparison would have rejected it.
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track.flac", ".flac", "FP_SAME", bitrate: null, size: 50_000_000),
            CreateSong(2, "/b/track.mp3", ".mp3", "FP_SAME", bitrate: 320, size: 10_000_000));
        await db.SaveChangesAsync();

        var service = CreateService(db, gate: new FakeGate(["FP_SAME"], similarity: 0.10));
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(1, result.GroupsFound);
        var link = Assert.Single(await db.SongDuplicateLinks.ToListAsync());
        Assert.Equal(DuplicateConfidence.Confirmed, link.Confidence);
        Assert.Equal(DuplicateMatchReason.ExactFingerprint, link.Reasons);
        Assert.Equal(1.0, link.Similarity!.Value, precision: 3);
    }

    [Fact]
    public async Task DetectDuplicates_OversizedBlock_IsSkippedWithoutPairingAnyMember()
    {
        // A block over DuplicateMaxBlockSize is pathological (one fingerprint on hundreds of files,
        // say) and is dropped whole rather than exploding into n² comparisons; other blocks in the
        // same run are unaffected.
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/1.mp3", ".mp3", "FP_BIG", bitrate: 320, size: 10_000_000, title: "Big One"),
            CreateSong(2, "/a/2.mp3", ".mp3", "FP_BIG", bitrate: 320, size: 10_000_000, title: "Big Two"),
            CreateSong(3, "/a/3.mp3", ".mp3", "FP_BIG", bitrate: 320, size: 10_000_000, title: "Big Three"),
            CreateSong(4, "/b/4.mp3", ".mp3", "FP_PAIR", bitrate: 320, size: 10_000_000, title: "Pair A"),
            CreateSong(5, "/b/5.mp3", ".mp3", "FP_PAIR", bitrate: 128, size: 4_000_000, title: "Pair B"));
        await db.SaveChangesAsync();

        var service = CreateService(db, options: new MusicEnricherOptions { DuplicateMaxBlockSize = 2 });
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(1, result.GroupsFound);
        var link = Assert.Single(await db.SongDuplicateLinks.ToListAsync());
        Assert.Equal((4, 5), (link.SongIdLow, link.SongIdHigh));
        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.All(songs.Take(3), s => Assert.False(s.IsDuplicate));
        Assert.True(songs[4].IsDuplicate);
    }

    [Fact]
    public async Task DetectDuplicates_MetadataBlock_KeysOnThePrimaryArtist()
    {
        // "Main Artist feat. Guest" and "Main Artist" are one recording's credit written two ways;
        // the block key uses the primary artist so a featuring credit can't hide a duplicate.
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track.mp3", ".mp3", null, bitrate: 320, size: 10_000_000,
                artist: "Main Artist feat. Guest", title: "Same Song", durationSeconds: 200),
            CreateSong(2, "/b/track.mp3", ".mp3", null, bitrate: 128, size: 4_000_000,
                artist: "Main Artist", title: "Same Song", durationSeconds: 202));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(1, result.SuspectedPairs);
        var link = Assert.Single(await db.SongDuplicateLinks.ToListAsync());
        Assert.Equal(DuplicateMatchReason.Metadata, link.Reasons);
    }

    [Fact]
    public async Task DetectDuplicates_MetadataBlock_RequiresBothDurations()
    {
        // Metadata agreement is the weakest signal, so it only counts when both durations are known
        // and agree — an unknown duration is not "close enough".
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track.mp3", ".mp3", null, bitrate: 320, size: 10_000_000, durationSeconds: 200),
            CreateSong(2, "/b/track.mp3", ".mp3", null, bitrate: 128, size: 4_000_000, durationSeconds: null));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(0, result.SuspectedPairs);
        Assert.Equal(0, await db.SongDuplicateLinks.CountAsync());
    }

    [Theory]
    [InlineData(200, 205, true)]   // within twice the 3s metadata tolerance
    [InlineData(200, 210, false)]  // outside it: a drifted tag or a different edit
    [InlineData(200, null, true)]  // an unknown duration never blocks an identifier match
    public async Task DetectDuplicates_SharedAcoustId_HonoursTheLooseDurationTolerance(
        int durationA, int? durationB, bool expectPair)
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/a.mp3", ".mp3", null, bitrate: 320, size: 10_000_000,
                title: "Title A", artist: "Artist A", durationSeconds: durationA, acoustIdTrackId: "acoustid-xyz"),
            CreateSong(2, "/b/b.mp3", ".mp3", null, bitrate: 128, size: 4_000_000,
                title: "Title B", artist: "Artist B", durationSeconds: durationB, acoustIdTrackId: "acoustid-xyz"));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(expectPair ? 1 : 0, result.SuspectedPairs);
        Assert.Equal(expectPair ? 1 : 0, await db.SongDuplicateLinks.CountAsync());
    }

    [Fact]
    public async Task DetectDuplicates_AccumulatesReasons_AcrossBlockingStrategies()
    {
        // One pair found by two independent strategies carries both reasons on its single link.
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track.mp3", ".mp3", null, bitrate: 320, size: 10_000_000,
                durationSeconds: 200, isrc: "USABC1234567"),
            CreateSong(2, "/b/track.mp3", ".mp3", null, bitrate: 128, size: 4_000_000,
                durationSeconds: 200, isrc: "USABC1234567"));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(1, result.SuspectedPairs);
        var link = Assert.Single(await db.SongDuplicateLinks.ToListAsync());
        Assert.Equal(DuplicateMatchReason.Isrc | DuplicateMatchReason.Metadata, link.Reasons);
        Assert.Equal(DuplicateConfidence.Suspected, link.Confidence);
    }

    private static SongMetadata CreateSong(
        int id,
        string sourcePath,
        string extension,
        string? fingerprint,
        int? bitrate,
        long size,
        Guid? owner = null,
        string title = "Test Track",
        string artist = "Test Artist",
        int? durationSeconds = null,
        string? acoustIdTrackId = null,
        string? isrc = null)
    {
        return new SongMetadata
        {
            OwnerUserId = owner ?? MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = sourcePath,
            FileName = Path.GetFileName(sourcePath),
            Extension = extension,
            FileSizeBytes = size,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Fingerprint = fingerprint,
            Bitrate = bitrate,
            Artist = artist,
            Title = title,
            DurationSeconds = durationSeconds,
            AcoustIdTrackId = acoustIdTrackId,
            Isrc = isrc,
        };
    }

    private static DuplicateDetectionService CreateService(
        MusicHoarderDbContext db,
        IFingerprintSimilarityGate? gate = null,
        MusicEnricherOptions? options = null)
    {
        var scopeFactory = new TestScopeFactory(db);
        return new DuplicateDetectionService(
            scopeFactory,
            new DuplicateCandidateGenerator(NullLogger<DuplicateCandidateGenerator>.Instance),
            new DuplicatePairConfirmer(gate ?? new FingerprintSimilarityGate()),
            new TestOptionsMonitor(options ?? new MusicEnricherOptions()),
            NullLogger<DuplicateDetectionService>.Instance);
    }

    private static MusicHoarderDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    /// <summary>Decodes only the listed fingerprint strings; every comparison returns the fixed
    /// similarity. Lets tests steer the confirm/suspect/reject verdict without real Chromaprints.</summary>
    private sealed class FakeGate(IEnumerable<string> decodable, double similarity) : IFingerprintSimilarityGate
    {
        private readonly HashSet<string> _decodable = [.. decodable];

        public bool TryDecode(string? compressed, out uint[] frames)
        {
            if (compressed is not null && _decodable.Contains(compressed))
            {
                frames = [1u];
                return true;
            }
            frames = [];
            return false;
        }

        public double Similarity(uint[] a, uint[] b) => similarity;
    }

    private sealed class TestOptionsMonitor(MusicEnricherOptions value) : IOptionsMonitor<MusicEnricherOptions>
    {
        public MusicEnricherOptions CurrentValue => value;
        public MusicEnricherOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<MusicEnricherOptions, string?> listener) => null;
    }

    private sealed class TestScopeFactory(MusicHoarderDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new TestScope(db);
    }

    private sealed class TestScope(MusicHoarderDbContext db) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new TestServiceProvider(db);
        public void Dispose() { }
    }

    private sealed class TestServiceProvider(MusicHoarderDbContext db) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(MusicHoarderDbContext)) return db;
            return null;
        }
    }
}
