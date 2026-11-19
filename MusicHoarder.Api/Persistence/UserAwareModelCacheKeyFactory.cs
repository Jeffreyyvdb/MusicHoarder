using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// EF caches the compiled model per <see cref="DbContext"/> type. The global query filters in
/// <see cref="MusicHoarderDbContext.OnModelCreating"/> capture whether the context has a user accessor
/// at all, and the secret-column protector is baked in as a value converter, so contexts that differ
/// in either must not share a model. This factory varies the cache key by exactly that (see
/// <see cref="MusicHoarderDbContext.ModelCacheKeySegment"/>). Whose rows the filters keep is read per
/// query, so every account shares one model.
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
