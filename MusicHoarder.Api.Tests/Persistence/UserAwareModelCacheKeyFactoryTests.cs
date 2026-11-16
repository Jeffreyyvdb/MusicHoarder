using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Persistence;

/// <summary>
/// The compiled model bakes the query filters in, so two contexts whose filters differ must never share a
/// cached model: whichever built it first would decide which rows every later context can see.
/// </summary>
public class UserAwareModelCacheKeyFactoryTests
{
    private static readonly DbContextOptions Options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    [Fact]
    public void a_context_with_no_accessor_never_shares_a_model_with_one_whose_accessor_has_no_user()
    {
        using var unfiltered = new MusicHoarderDbContext(Options);
        using var signedOut = new MusicHoarderDbContext(Options, new TestCurrentUserAccessor());

        Assert.NotEqual(KeyOf(unfiltered), KeyOf(signedOut));
    }

    [Fact]
    public void contexts_for_the_same_user_share_a_model()
    {
        using var first = new MusicHoarderDbContext(Options, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser));
        using var second = new MusicHoarderDbContext(Options, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser));

        Assert.Equal(KeyOf(first), KeyOf(second));
    }

    private static object KeyOf(DbContext context) => new UserAwareModelCacheKeyFactory().Create(context, designTime: false);
}
