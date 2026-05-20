using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// EF caches the compiled model per <see cref="DbContext"/> type. Because we bake the current
/// user's id into the global query filters in <see cref="MusicHoarderDbContext.OnModelCreating"/>,
/// two contexts with different users would otherwise share the same compiled model and the
/// wrong filter value. This factory varies the cache key by the captured user id (and "anonymous"
/// for design-time / background-service contexts), so each variant gets its own compiled model.
/// At our 2-user scale this caches at most 3 models (Owner / Demo / anonymous).
/// </summary>
public sealed class UserAwareModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        if (context is MusicHoarderDbContext mh)
            return (context.GetType(), designTime, mh.ModelCacheKeySegment);
        return (context.GetType(), designTime);
    }
}
