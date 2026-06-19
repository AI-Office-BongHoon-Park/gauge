using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Gauge.Models;
using Gauge.Providers.Internal;
using Gauge.Services;

namespace Gauge.Providers;

/// <summary>
/// Reads Codex usage from the ChatGPT backend usage endpoint
/// (<c>GET https://chatgpt.com/backend-api/wham/usage</c>) using the OAuth token the
/// Codex CLI stores in <c>~/.codex/auth.json</c>. This returns the real 5-hour
/// (primary) and weekly (secondary) rate-limit utilization and reset times, plus the
/// plan tier — the same data the CLI itself sees, and always current (unlike scanning
/// local session logs, which go stale once Codex hasn't run for a while).
///
/// A missing credential is a clean empty-data result. Network and API failures
/// propagate so the coordinator keeps showing its last good snapshot rather than
/// replacing it with an empty success.
/// </summary>
public sealed class CodexProvider : IUsageProvider
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

    private readonly HttpClient _http;
    private readonly ICredentialSource _credentials;

    public CodexProvider(HttpClient http, ICredentialSource credentials)
    {
        _http = http;
        _credentials = credentials;
    }

    public ToolKind Tool => ToolKind.Codex;
    public string ToolName => ToolCatalog.For(ToolKind.Codex).DisplayName;

    public async Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var credentialResult = await _credentials.ReadAsync(ToolKind.Codex, cancellationToken);
        var credentials = credentialResult.Credential;
        AppLog.Write($"Codex refresh credentialStatus={credentialResult.Status} hasToken={credentials?.AccessToken is { Length: > 0 }} hasAccountId={!string.IsNullOrEmpty(credentials?.AccountId)}");

        if (credentialResult.Status == CredentialReadStatus.Invalid)
        {
            AppLog.Write("Codex refresh rejected: invalid credential");
            throw new AuthenticationRequiredException(ToolKind.Codex, HttpStatusCode.Unauthorized);
        }

        // No token (not logged in): a legitimate "no data yet" state, not a failure.
        if (credentials?.AccessToken is not { Length: > 0 } token)
        {
            AppLog.Write("Codex refresh skipped: missing token");
            return new UsageSnapshot
            {
                ToolName = ToolName,
                Plan = null,
                Windows = Array.Empty<UsageWindow>(),
                CapturedAt = DateTimeOffset.Now,
            };
        }

        // wham/usage is the same endpoint the Codex CLI itself polls every 60s, so our
        // 60s cadence needs no extra throttling. Let fetch failures (network/429)
        // propagate rather than swallowing them into an empty success — that way the
        // coordinator keeps the last good snapshot instead of clearing the card.
        try
        {
            var (plan, windows) = await FetchUsageAsync(token, credentials.AccountId, cancellationToken);
            return new UsageSnapshot
            {
                ToolName = ToolName,
                Plan = plan,
                Windows = windows,
                CapturedAt = DateTimeOffset.Now,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } httpError)
            {
                AppLog.Write($"Codex refresh auth failed: status={(int)httpError.StatusCode!.Value}");
                throw new AuthenticationRequiredException(ToolKind.Codex, httpError.StatusCode!.Value);
            }
            AppLog.Write($"Codex refresh failed: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private async Task<(string? Plan, List<UsageWindow> Windows)> FetchUsageAsync(
        string token, string? accountId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("User-Agent", "Gauge/1.0");
        if (!string.IsNullOrEmpty(accountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
        }

        AppLog.Write($"Codex usage request started accountHeader={!string.IsNullOrEmpty(accountId)}");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        AppLog.Write($"Codex usage response status={(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, default, cancellationToken);
        var root = document.RootElement;

        var planType = root.GetStringOrNull("plan_type");
        var plan = MapPlan(planType);
        AppLog.Write($"Codex usage parsed planType={planType ?? "<missing>"} plan={plan ?? "<missing>"} rootKeys={SafeKeys(root)} hasRateLimit={root.GetObjectOrNull("rate_limit") is not null} hasCredits={root.GetObjectOrNull("credits") is not null}");

        var windows = new List<UsageWindow>();
        if (root.GetObjectOrNull("rate_limit") is { } rateLimit)
        {
            if (ParseWindow(rateLimit, "primary_window", UsageWindowType.FiveHour, "5시간") is { } fiveHour)
            {
                windows.Add(fiveHour);
            }
            if (ParseWindow(rateLimit, "secondary_window", UsageWindowType.Weekly, "주간") is { } weekly)
            {
                windows.Add(weekly);
            }
        }

        if (windows.Count == 0 || plan == "Enterprise")
        {
            if (ParseCredits(root) is { } credits)
            {
                windows.Add(credits);
                AppLog.Write($"Codex enterprise credits parsed hasDetail={!string.IsNullOrEmpty(credits.DetailText)} usedRatio={credits.UsedRatio:0.###}");
            }
            else
            {
                LogCreditShape(root);
            }
        }

        AppLog.Write($"Codex usage refresh succeeded windows={windows.Count}");
        return (plan, windows);
    }

    /// <summary>
    /// Parses one rate-limit window: <c>{ "used_percent": 0–100, "reset_at": epochSeconds }</c>.
    /// </summary>
    private static UsageWindow? ParseWindow(JsonElement rateLimit, string property, UsageWindowType type, string label)
    {
        if (rateLimit.GetObjectOrNull(property) is not { } window
            || window.GetDoubleOrNull("used_percent") is not { } usedPercent)
        {
            return null;
        }

        var resetTime = window.GetInt64OrNull("reset_at") is { } epoch
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : (DateTimeOffset?)null;

        return new UsageWindow
        {
            Type = type,
            UsedRatio = Math.Clamp(usedPercent / 100.0, 0.0, 1.0),
            Label = label,
            ResetTime = resetTime,
        };
    }

    private static UsageWindow? ParseCredits(JsonElement root)
    {
        var spendControl = root.GetObjectOrNull("spend_control");
        if (root.GetObjectOrNull("credits") is { } credits
            && GetStringOrNumber(credits, "balance") is { Length: > 0 } balance)
        {
            var limit = spendControl is { } control ? GetDouble(control, "individual_limit", "indivisual_limit") : null;
            var remaining = double.TryParse(balance, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedBalance)
                ? parsedBalance
                : (double?)null;
            var usedRatio = limit is > 0 && remaining is { } value
                ? Math.Clamp((limit.Value - value) / limit.Value, 0.0, 1.0)
                : 0.0;
            var detail = limit is > 0
                ? $"잔액 {FormatMoney(balance)} / {FormatMoney(limit.Value.ToString(CultureInfo.InvariantCulture))}"
                : $"잔액 {FormatMoney(balance)}";

            return new UsageWindow
            {
                Type = UsageWindowType.BillingCycle,
                UsedRatio = usedRatio,
                Label = "크레딧",
                DetailText = detail,
            };
        }

        if (TryGetProperty(spendControl, out var limitObject, "individual_limit", "indivisual_limit")
            && limitObject.ValueKind == JsonValueKind.Object)
        {
            return ParseLimitObject(limitObject, spendControl);
        }

        return null;
    }

    private static UsageWindow? ParseLimitObject(JsonElement limitObject, JsonElement? spendControl)
    {
        var percent = GetDouble(limitObject, "used_percent", "percent_used", "utilization", "usedPercentage");
        var limit = GetDouble(limitObject, "limit", "total", "amount", "max", "hard_limit", "soft_limit");
        var used = GetDouble(limitObject, "used", "current", "consumed", "spend", "spent");
        var remaining = GetDouble(limitObject, "remaining", "balance", "available");
        var reached = spendControl is { } control && GetBool(control, "reached") == true;

        var usedRatio = percent is { } p
            ? Math.Clamp(p / 100.0, 0.0, 1.0)
            : limit is > 0 && used is { } u
                ? Math.Clamp(u / limit.Value, 0.0, 1.0)
                : limit is > 0 && remaining is { } r
                    ? Math.Clamp((limit.Value - r) / limit.Value, 0.0, 1.0)
                    : reached ? 1.0 : 0.0;

        var detail = remaining is { } rem && limit is { } l1
            ? $"잔액 {FormatMoney(rem.ToString(CultureInfo.InvariantCulture))} / {FormatMoney(l1.ToString(CultureInfo.InvariantCulture))}"
            : remaining is { } remOnly
                ? $"잔액 {FormatMoney(remOnly.ToString(CultureInfo.InvariantCulture))}"
                : used is { } u2 && limit is { } l2
                    ? $"{FormatMoney(u2.ToString(CultureInfo.InvariantCulture))} / {FormatMoney(l2.ToString(CultureInfo.InvariantCulture))}"
                : reached ? "한도 도달" : "한도";

        if (percent is null && limit is null && used is null && remaining is null
            && (spendControl is not { } control2 || GetBool(control2, "reached") is null))
        {
            return null;
        }

        return new UsageWindow
        {
            Type = UsageWindowType.BillingCycle,
            UsedRatio = usedRatio,
            Label = "크레딧",
            DetailText = detail,
        };
    }

    private static void LogCreditShape(JsonElement root)
    {
        var credits = root.GetObjectOrNull("credits");
        var spendControl = root.GetObjectOrNull("spend_control");
        AppLog.Write("Codex enterprise credits missing or unparseable "
            + $"creditsKeys={SafeKeys(credits)} "
            + $"balanceKind={PropertyKind(credits, "balance")} "
            + $"spendControlKeys={SafeKeys(spendControl)} "
            + $"individualLimitKind={PropertyKind(spendControl, "individual_limit")} "
            + $"indivisualLimitKind={PropertyKind(spendControl, "indivisual_limit")} "
            + $"individualLimitShape={ObjectShape(spendControl, "individual_limit")} "
            + $"indivisualLimitShape={ObjectShape(spendControl, "indivisual_limit")}");
    }

    private static string SafeKeys(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj)
        {
            return "<none>";
        }

        var keys = obj.EnumerateObject().Select(property => property.Name).Take(24);
        return string.Join(",", keys);
    }

    private static string PropertyKind(JsonElement? element, string property)
        => element is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(property, out var value)
            ? value.ValueKind.ToString()
            : "<missing>";

    private static string ObjectShape(JsonElement? element, string property)
    {
        if (!TryGetProperty(element, out var value, property) || value.ValueKind != JsonValueKind.Object)
        {
            return "<none>";
        }

        var parts = value.EnumerateObject()
            .Take(24)
            .Select(item => $"{item.Name}:{item.Value.ValueKind}");
        return string.Join(",", parts);
    }

    private static string? GetStringOrNumber(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static double? GetDouble(JsonElement element, params string[] properties)
    {
        if (!TryGetProperty(element, out var value, properties))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
               && double.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static bool? GetBool(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static bool TryGetProperty(JsonElement? element, out JsonElement value, params string[] properties)
    {
        if (element is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var property in properties)
            {
                if (obj.TryGetProperty(property, out value))
                {
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string FormatMoney(string value) => value.StartsWith('$') ? value : $"${value}";

    private static string? MapPlan(string? planType) => planType?.ToLowerInvariant() switch
    {
        null or "" => null,
        "plus" => "Plus",
        "pro" => "Pro",
        "free" => "Free",
        "go" => "Go",
        "business" => "Business",
        "team" => "Team",
        "enterprise" => "Enterprise",
        var other => char.ToUpperInvariant(other[0]) + other[1..],
    };
}
