using MusicHoarder.Api.Auth;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// The one place that expresses "exclude the demo tenant's rows". All-tenant background sweeps run with
/// <c>IgnoreQueryFilters()</c> (the per-user EF filter is off), so each one must re-exclude the demo
/// user's rows by hand — and forgetting to has repeatedly let demo data leak into enrichment, library
/// builds, dedup, cover-art and grading. Centralizing the clause makes the intent greppable and the
/// omission obvious in review.
/// </summary>
/// <remarks>
/// This is the demo-tenant clause ONLY. It is deliberately <b>not</b> the same as <c>!IsSynthetic</c>:
/// demo rows seeded from real media are <c>IsSynthetic == false</c>, so filtering synthetic rows does
/// not exclude the demo tenant. Call sites keep their own <c>!IsSynthetic</c>, <c>DeletedAtUtc</c>,
/// status and other predicates; this only drops the demo owner.
/// </remarks>
public static class DemoTenantQueryExtensions
{
    public static IQueryable<SongMetadata> ExcludingDemoTenant(this IQueryable<SongMetadata> songs) =>
        songs.Where(s => s.OwnerUserId != WellKnownUsers.DemoId);

    public static IQueryable<WishlistItem> ExcludingDemoTenant(this IQueryable<WishlistItem> items) =>
        items.Where(w => w.OwnerUserId != WellKnownUsers.DemoId);
}
