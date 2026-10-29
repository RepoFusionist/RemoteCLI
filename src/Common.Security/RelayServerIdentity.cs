using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Security;

public static class RelayClientIdentity
{
    public const string HeaderName = "X-RemoteCLI-Client";
    public const string HeaderValue = "RemoteCLI/1";
}

public sealed record RelayServerHealth(
    string ServerFingerprintSha256,
    bool AdminInitialized,
    bool OfficialClientsOnly);

public static class RelayServerIdentityClient
{
    // NewServer/ 的固定实现指纹。只有匹配这个静态指纹时才跳过首次连接警告。
    public const string OfficialServerFingerprintSha256 =
        "DCED4BCFDB98516EA9C3F7E44FE0031CE413C43B9906CAC025DA66DF1EFF54D3";

    private static readonly JsonSerializerOptions HealthJsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    public static async Task<RelayServerHealth> ReadAsync(
        HttpClient client,
        Uri serverUri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(serverUri, "healthz"));
        request.Headers.TryAddWithoutValidation(RelayClientIdentity.HeaderName, RelayClientIdentity.HeaderValue);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RelayAuthorizationException(
                "relay_health_failed",
                "无法读取 Relay 服务端状态（HTTP " + (int)response.StatusCode + "），连接已暂停。",
                response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                    (int)response.StatusCode >= 500);
        }

        try
        {
            var payload = await response.Content.ReadFromJsonAsync<RelayHealthPayload>(HealthJsonOptions, cancellationToken)
                .ConfigureAwait(false);
            var fingerprint = payload?.ServerFingerprintSha256?.Trim().ToUpperInvariant();
            if (!IsSha256(fingerprint))
            {
                throw new InvalidDataException("Relay 未返回有效的服务端 SHA-256 指纹；请先更新服务端。连接已暂停。");
            }

            return new RelayServerHealth(fingerprint!, payload!.AdminInitialized, payload.OfficialClientsOnly);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Relay 返回的服务端状态格式无效；请检查地址和反向代理路由。");
        }
    }

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => Uri.IsHexDigit(character));

    public static bool IsOfficialServerFingerprint(string? value) =>
        string.Equals(value?.Trim(), OfficialServerFingerprintSha256, StringComparison.OrdinalIgnoreCase);

    private sealed record RelayHealthPayload(
        string? ServerFingerprintSha256,
        bool AdminInitialized,
        bool OfficialClientsOnly);
}
