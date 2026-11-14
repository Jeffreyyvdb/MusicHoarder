using System.Reflection;
using System.Text;
using Fido2NetLib;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>Passkey sign-in challenges are single-use and expire on the server.</summary>
public class WebAuthnChallengeSingleUseTests
{
    [Fact]
    public async Task Passkey_sign_in_challenge_is_accepted_once_and_refused_on_replay()
    {
        using var fixture = new Fixture();
        var cookie = await fixture.BeginAsync();

        Assert.Equal(400, await fixture.CompleteAsync(cookie)); // rejected assertion, but the challenge was consumed
        Assert.Equal(1, fixture.Service.CompleteCalls);

        Assert.Equal(400, await fixture.CompleteAsync(cookie));
        Assert.Equal(1, fixture.Service.CompleteCalls);
    }

    [Fact]
    public async Task Passkey_sign_in_challenge_is_refused_after_its_server_side_lifetime()
    {
        using var fixture = new Fixture();
        var cookie = await fixture.BeginAsync();

        ((MemoryCache)fixture.Cache).Compact(1.0); // drop every cached entry, as expiry would

        Assert.Equal(400, await fixture.CompleteAsync(cookie));
        Assert.Equal(0, fixture.Service.CompleteCalls);
    }

    [Fact]
    public async Task Passkey_sign_in_reaches_the_service_for_a_fresh_challenge()
    {
        using var fixture = new Fixture();
        await fixture.CompleteAsync(await fixture.BeginAsync());
        await fixture.CompleteAsync(await fixture.BeginAsync());
        Assert.Equal(2, fixture.Service.CompleteCalls);
    }

    private sealed class FakeWebAuthn : IWebAuthnService
    {
        public int CompleteCalls;

        public AssertionOptions BeginAuthentication() =>
            new AssertionOptions { Challenge = Guid.NewGuid().ToByteArray(), RpId = "localhost" };

        public Task<PasskeySignInResult> CompleteAuthenticationAsync(
            AuthenticatorAssertionRawResponse assertion, AssertionOptions originalOptions, string? ip, string? userAgent, CancellationToken ct)
        {
            Interlocked.Increment(ref CompleteCalls);
            return Task.FromResult(PasskeySignInResult.Rejected);
        }

        public Task<CredentialCreateOptions?> BeginRegistrationAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<WebAuthnCredentialView> CompleteRegistrationAsync(Guid userId, AuthenticatorAttestationRawResponse attestation, CredentialCreateOptions originalOptions, string displayName, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<WebAuthnCredentialView>> ListCredentialsAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteCredentialAsync(Guid userId, Guid credentialId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PasskeySignalInfo?> GetSignalInfoAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException();
    }

    private class NullProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _http;

        public FakeWebAuthn Service { get; } = new();
        public IMemoryCache Cache { get; }

        public Fixture()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
            builder.Logging.ClearProviders();
            builder.Services.AddMemoryCache();
            builder.Services.AddDataProtection();
            builder.Services.AddSingleton<IWebAuthnService>(Service);
            builder.Services.AddSingleton(DispatchProxy.Create<IAccountSwitchService, NullProxy>());
            builder.Services.AddSingleton(DispatchProxy.Create<ICurrentUserAccessor, NullProxy>());
            _app = builder.Build();
            ((IEndpointRouteBuilder)_app).MapWebAuthnEndpoints();
            _app.StartAsync().GetAwaiter().GetResult();
            Cache = _app.Services.GetRequiredService<IMemoryCache>();
            var address = _app.Urls.First();
            _http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(address) };
        }

        public async Task<string> BeginAsync()
        {
            using var response = await _http.PostAsync("/api/auth/webauthn/authenticate/begin", null);
            response.EnsureSuccessStatusCode();
            var setCookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("mh_webauthn_auth="));
            return setCookie.Split(';')[0].Split('=', 2)[1];
        }

        public async Task<int> CompleteAsync(string cookieValue)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new AuthenticatorAssertionRawResponse
            {
                Id = "AQID",
                RawId = [1, 2, 3],
                Type = Fido2NetLib.Objects.PublicKeyCredentialType.PublicKey,
                Response = new AuthenticatorAssertionRawResponse.AssertionResponse
                {
                    AuthenticatorData = [1, 2, 3],
                    ClientDataJson = [1, 2, 3],
                    Signature = [1, 2, 3],
                },
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/webauthn/authenticate/complete")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Cookie", "mh_webauthn_auth=" + cookieValue);
            using var response = await _http.SendAsync(request);
            return (int)response.StatusCode;
        }

        public void Dispose()
        {
            _http.Dispose();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
