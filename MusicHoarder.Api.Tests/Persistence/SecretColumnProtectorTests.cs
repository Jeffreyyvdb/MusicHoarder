using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Persistence;

/// <summary>
/// The EF InMemory provider keeps model values, not converted ones, so these tests cannot read
/// "what the database holds". They pin the parts that decide it instead: the protector, the
/// converter EF is given for each column, and which rows the startup step rewrites.
/// </summary>
public class SecretColumnProtectorTests
{
    private static readonly string[] SecretColumns =
        [nameof(SpotifySettings.ClientSecret), nameof(SpotifySettings.AccessToken), nameof(SpotifySettings.RefreshToken)];

    [Fact]
    public void Protect_marks_and_encrypts_and_unprotect_reverses_it()
    {
        var protector = NewProtector();

        var stored = protector.Protect("refresh-token");

        Assert.StartsWith(SecretColumnProtector.Prefix, stored);
        Assert.DoesNotContain("refresh-token", stored);
        Assert.Equal("refresh-token", protector.Unprotect(stored));
    }

    [Fact]
    public void A_value_written_before_encryption_reads_unchanged()
    {
        Assert.Equal("plain-token", NewProtector().Unprotect("plain-token"));
    }

    [Fact]
    public void A_value_from_another_key_ring_reads_as_null()
    {
        // What a lost keys directory looks like: the row reads as not connected rather than throwing.
        var stored = NewProtector().Protect("refresh-token");

        Assert.Null(NewProtector().Unprotect(stored));
    }

    [Fact]
    public void The_context_DI_builds_encrypts_the_three_secret_columns_and_nothing_else()
    {
        var protector = NewProtector();
        using var db = new MusicHoarderDbContext(NewOptions(), new NoUser(), protector);
        var entity = db.Model.FindEntityType(typeof(SpotifySettings))!;

        foreach (var column in SecretColumns)
        {
            var converter = entity.FindProperty(column)!.GetValueConverter();
            Assert.IsType<ProtectedStringConverter>(converter);
            var written = (string)converter!.ConvertToProvider("value")!;
            Assert.StartsWith(SecretColumnProtector.Prefix, written);
            Assert.Equal("value", converter.ConvertFromProvider(written));
        }

        // The client id is shown in Settings and is not a secret.
        Assert.Null(entity.FindProperty(nameof(SpotifySettings.ClientId))!.GetValueConverter());
    }

    [Fact]
    public void A_context_without_a_protector_stores_the_columns_as_given()
    {
        using var db = new MusicHoarderDbContext(NewOptions());
        var entity = db.Model.FindEntityType(typeof(SpotifySettings))!;

        foreach (var column in SecretColumns)
            Assert.Null(entity.FindProperty(column)!.GetValueConverter());
    }

    [Fact]
    public void Contexts_with_different_protectors_never_share_a_cached_model()
    {
        // The protector lives in the model as a converter; a shared model would decrypt with the
        // wrong keys.
        var options = NewOptions();
        var first = NewProtector();
        var second = NewProtector();
        using var a = new MusicHoarderDbContext(options, new NoUser(), first);
        using var b = new MusicHoarderDbContext(options, new NoUser(), second);

        var fromA = a.Model.FindEntityType(typeof(SpotifySettings))!.FindProperty(nameof(SpotifySettings.RefreshToken))!.GetValueConverter()!;
        var fromB = b.Model.FindEntityType(typeof(SpotifySettings))!.FindProperty(nameof(SpotifySettings.RefreshToken))!.GetValueConverter()!;

        Assert.Equal("v", first.Unprotect((string)fromA.ConvertToProvider("v")!));
        Assert.Equal("v", second.Unprotect((string)fromB.ConvertToProvider("v")!));
    }

    [Fact]
    public async Task Startup_rewrites_only_rows_that_still_hold_plain_text()
    {
        var options = NewOptions();
        var protector = NewProtector();
        await using (var plain = new MusicHoarderDbContext(options))
        {
            plain.SpotifySettings.AddRange(
                new SpotifySettings { OwnerUserId = Guid.NewGuid(), RefreshToken = "legacy-refresh" },
                new SpotifySettings { OwnerUserId = Guid.NewGuid(), ClientSecret = "legacy-secret" },
                new SpotifySettings
                {
                    OwnerUserId = Guid.NewGuid(),
                    ClientSecret = protector.Protect("s"),
                    AccessToken = protector.Protect("a"),
                    RefreshToken = protector.Protect("r"),
                },
                new SpotifySettings { OwnerUserId = Guid.NewGuid() });
            await plain.SaveChangesAsync();
        }

        Assert.Equal(2, await LegacySecretColumns.ProtectAsync(options, new NoUser(), protector));
    }

    [Fact]
    public async Task Startup_does_nothing_when_no_row_holds_plain_text()
    {
        var options = NewOptions();
        await using (var plain = new MusicHoarderDbContext(options))
        {
            plain.SpotifySettings.Add(new SpotifySettings { OwnerUserId = Guid.NewGuid(), ClientId = "id" });
            await plain.SaveChangesAsync();
        }

        Assert.Equal(0, await LegacySecretColumns.ProtectAsync(options, new NoUser(), NewProtector()));
    }

    private static SecretColumnProtector NewProtector() => new(new EphemeralDataProtectionProvider());

    private static DbContextOptions<MusicHoarderDbContext> NewOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private sealed class NoUser : ICurrentUserAccessor
    {
        public CurrentUser? User => null;
        public Guid UserId => Guid.Empty;
    }
}
