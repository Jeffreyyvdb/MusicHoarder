using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// Rename-self via PATCH /api/auth/me: persists the trimmed name, folds empty to null (UI falls
/// back to the email), rejects over-long names, and 401s without a session. Demo/friend denial is
/// covered by the read-only middleware tests, not here — the handler itself only needs a session.
/// </summary>
public class AuthEndpointsTests
{
    [Fact]
    public async Task UpdateMe_renames_and_returns_updated_user()
    {
        var options = Setup();
        await using var db = new MusicHoarderDbContext(options);

        var result = await AuthEndpoints.UpdateMe(
            new UpdateMeBody("  Jeffrey  "), OwnerAccessor(), db, CancellationToken.None);

        var value = Value(result);
        Assert.Equal("Jeffrey", GetProperty<string?>(value, "displayName"));
        Assert.Equal("owner@example.com", GetProperty<string>(value, "email"));
        Assert.Equal("Owner", GetProperty<string>(value, "role"));

        await using var verify = new MusicHoarderDbContext(options);
        Assert.Equal("Jeffrey", (await verify.Users.SingleAsync(u => u.Id == TestUsers.OwnerId)).DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateMe_empty_name_clears_to_null(string? input)
    {
        var options = Setup();
        await using var db = new MusicHoarderDbContext(options);

        var result = await AuthEndpoints.UpdateMe(
            new UpdateMeBody(input), OwnerAccessor(), db, CancellationToken.None);

        Assert.Null(GetProperty<string?>(Value(result), "displayName"));

        await using var verify = new MusicHoarderDbContext(options);
        Assert.Null((await verify.Users.SingleAsync(u => u.Id == TestUsers.OwnerId)).DisplayName);
    }

    [Fact]
    public async Task UpdateMe_over_100_chars_is_400_and_does_not_persist()
    {
        var options = Setup();
        await using var db = new MusicHoarderDbContext(options);

        var result = await AuthEndpoints.UpdateMe(
            new UpdateMeBody(new string('x', 101)), OwnerAccessor(), db, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);

        await using var verify = new MusicHoarderDbContext(options);
        Assert.Equal("Owner", (await verify.Users.SingleAsync(u => u.Id == TestUsers.OwnerId)).DisplayName);
    }

    [Fact]
    public async Task UpdateMe_unauthenticated_is_401()
    {
        var options = Setup();
        await using var db = new MusicHoarderDbContext(options);

        var result = await AuthEndpoints.UpdateMe(
            new UpdateMeBody("Anyone"), new TestCurrentUserAccessor(null), db, CancellationToken.None);

        Assert.Equal(StatusCodes.Status401Unauthorized, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task UpdateMe_disabled_user_is_403()
    {
        var options = Setup();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            var owner = await seed.Users.SingleAsync(u => u.Id == TestUsers.OwnerId);
            owner.IsDisabled = true;
            await seed.SaveChangesAsync();
        }

        await using var db = new MusicHoarderDbContext(options);
        var result = await AuthEndpoints.UpdateMe(
            new UpdateMeBody("Jeffrey"), OwnerAccessor(), db, CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, ((IStatusCodeHttpResult)result).StatusCode);
    }

    // -- helpers --

    private static DbContextOptions<MusicHoarderDbContext> Setup()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var seed = new MusicHoarderDbContext(options);
        seed.Users.Add(new User
        {
            Id = TestUsers.OwnerId,
            Email = "owner@example.com",
            EmailNormalized = User.Normalize("owner@example.com"),
            DisplayName = "Owner",
            Role = UserRole.Admin,
            CreatedAtUtc = DateTime.UtcNow,
        });
        seed.SaveChanges();

        return options;
    }

    private static TestCurrentUserAccessor OwnerAccessor() =>
        new(TestCurrentUserAccessor.OwnerUser);

    private static object Value(IResult result)
        => result.GetType().GetProperty("Value")!.GetValue(result)!;

    private static T GetProperty<T>(object obj, string name)
    {
        var prop = obj.GetType().GetProperty(name);
        Assert.NotNull(prop);
        return (T)prop!.GetValue(obj)!;
    }
}
