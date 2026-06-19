using System.Net;
using System.Text;
using Gauge.Models;
using Gauge.Providers;
using Gauge.Services;

namespace Gauge.Tests;

public sealed class ProviderCredentialSwitchTests
{
    [Fact]
    public async Task ClaudeAccountSwitchInvalidatesProviderCache()
    {
        var source = new MutableSource("token-one");
        var handler = new CountingHandler();
        var provider = new ClaudeProvider(new HttpClient(handler), source);
        await provider.GetSnapshotAsync(default);
        await provider.GetSnapshotAsync(default);
        Assert.Equal(1, handler.CallCount);

        source.Token = "token-two";
        await provider.GetSnapshotAsync(default);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task UnauthorizedUsageBecomesAuthenticationRequired()
    {
        var source = new MutableSource("expired");
        var handler = new CountingHandler(HttpStatusCode.Unauthorized);
        var provider = new ClaudeProvider(new HttpClient(handler), source);
        var error = await Assert.ThrowsAsync<AuthenticationRequiredException>(() => provider.GetSnapshotAsync(default));
        Assert.Equal(ToolKind.ClaudeCode, error.Tool);
    }

    [Fact]
    public async Task ClaudeParsesNestedWindowUtilization()
    {
        var json = """{"five_hour":{"window":{"used_percent":"12.5","reset_at":"2026-07-01T00:00:00Z"}}}""";
        var provider = new ClaudeProvider(new HttpClient(new CountingHandler(json: json)), new MutableSource("token"));

        var snapshot = await provider.GetSnapshotAsync(default);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(UsageWindowType.FiveHour, window.Type);
        Assert.Equal(0.125, window.UsedRatio, 3);
        Assert.NotNull(window.ResetTime);
    }

    [Fact]
    public async Task ClaudeParsesSpendWhenWindowsAreNull()
    {
        var json = """{"five_hour":null,"seven_day":null,"spend":{"used":{"amount_minor":2500,"currency":"USD","exponent":2},"limit":{"amount_minor":10000,"currency":"USD","exponent":2},"percent":25,"severity":"ok","enabled":true,"disabled_reason":null}}""";
        var provider = new ClaudeProvider(new HttpClient(new CountingHandler(json: json)), new MutableSource("token"));

        var snapshot = await provider.GetSnapshotAsync(default);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(UsageWindowType.BillingCycle, window.Type);
        Assert.Equal("잔액 $75 / $100", window.DetailText);
        Assert.Equal(0.25, window.UsedRatio, 3);
    }

    private sealed class MutableSource(string token) : ICredentialSource
    {
        public string Token { get; set; } = token;
        public CredentialOwner Owner => CredentialOwner.CliLocal;
        public CredentialSource Source => CredentialSource.CliLocal;
        public Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default)
            => Task.FromResult(new CredentialReadResult
            {
                Tool = tool, Status = CredentialReadStatus.Available,
                Credential = new ToolCredential { Tool = tool, Owner = Owner, Source = Source, AccessToken = Token },
            });
    }

    private sealed class CountingHandler(HttpStatusCode statusCode = HttpStatusCode.OK, string json = "{\"five_hour\":{\"utilization\":10}}") : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
