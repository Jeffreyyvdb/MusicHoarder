using System.Globalization;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

/// <summary>
/// The shape every curation tool's audit rows share: one action is one batch, so every row carries
/// the same <see cref="SongMetadataChange.CreatedAtUtc"/> (the key <c>DedupActionHistoryService</c>
/// lists and reverts on), is applied at that same instant, has confidence 1.0, is not reverted, and
/// records the value the song now holds.
/// </summary>
internal static class AuditBatchAssert
{
    public static void OneAppliedBatch(
        IReadOnlyCollection<SongMetadataChange> rows, IReadOnlyCollection<SongMetadata> songs)
    {
        Assert.NotEmpty(rows);
        var stamp = Assert.Single(rows.Select(c => c.CreatedAtUtc).Distinct());
        var byId = songs.ToDictionary(s => s.Id);
        Assert.All(rows, c =>
        {
            Assert.Equal(1.0, c.Confidence);
            Assert.Equal(stamp, c.AppliedAtUtc);
            Assert.Null(c.RevertedAtUtc);
            Assert.NotEqual(c.OldValue, c.NewValue);
            var current = typeof(SongMetadata).GetProperty(c.FieldName)!.GetValue(byId[c.SongId]);
            Assert.Equal(current is null ? null : Convert.ToString(current, CultureInfo.InvariantCulture), c.NewValue);
        });
    }
}
