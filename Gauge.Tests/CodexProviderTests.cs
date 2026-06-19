using System.Net;
using System.Text;
using Gauge.Models;
using Gauge.Providers;
using Gauge.Services;

namespace Gauge.Tests;

public sealed class CodexProviderTests
{
    [Fact]
    public async Task ParsesCreditBalanceWhenRateLimitIsAbsent()
    {
        const string json = """
        {
          "plan_type": "enterprise",
          "credits": { "balance": "42.50" },
          "spend_control": { "individual_limit": 100.0 }
        }
        """;
        var provider = new CodexProvider(new HttpClient(new StubHandler(json)), Source());

        var snapshot = await provider.GetSnapshotAsync(default);

        Assert.Equal("Enterprise", snapshot.Plan);
        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(UsageWindowType.BillingCycle, window.Type);
        Assert.Equal("잔액 $42.50", window.DetailText);
        Assert.Equal(0.575, window.UsedRatio, 3);
    }

    private static ICredentialSource Source() => new StubSource(
        new CredentialReadResult
        {
            Tool = ToolKind.Codex,
            Status = CredentialReadStatus.Available,
            Credential = new ToolCredential
            {
                Tool = ToolKind.Codex,
                Owner = CredentialOwner.CliLocal,
                Source = CredentialSource.CliLocal,
                AccessToken = "token",
                AccountId = "acct",
            },
        });

    private sealed class StubSource(CredentialReadResult result) : ICredentialSource
    {
        public CredentialOwner Owner => CredentialOwner.CliLocal;
        public CredentialSource Source => CredentialSource.CliLocal;
        public Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
