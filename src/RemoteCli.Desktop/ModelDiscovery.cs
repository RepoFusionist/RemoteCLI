using System.Text.Json;

namespace RemoteCli.Desktop;

internal sealed partial class MainForm
{
    private async Task DiscoverModelsAsync(JsonElement root)
    {
        var baseUrl = ReadString(root, "baseUrl").Trim().TrimEnd('/');
        var apiKey = ReadString(root, "apiKey");
        var backend = ReadString(root, "backend");

        void Fail(string error) => PostToUi(new { type = "models-discovered", error });

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback))
        {
            Fail("请先填写有效的 HTTPS 网关地址。");
            return;
        }

        if (string.Equals(backend, "azure", StringComparison.OrdinalIgnoreCase))
        {
            Fail("Azure 后端不支持模型发现，请手动添加模型。");
            return;
        }

        try
        {
            var isAnthropic = string.Equals(backend, "anthropic", StringComparison.OrdinalIgnoreCase);
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var uri = isAnthropic
                ? new Uri(endpoint.AbsoluteUri.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? endpoint + "/models" : endpoint + "/v1/models")
                : new Uri(endpoint + "/models");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (isAnthropic)
            {
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            }
            else if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            }

            using var response = await httpClient.SendAsync(request, _serviceLifetime.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Fail("模型发现失败：HTTP " + (int)response.StatusCode + "，请检查地址、密钥和后端类型。");
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(_serviceLifetime.Token).ConfigureAwait(false);
            using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: _serviceLifetime.Token).ConfigureAwait(false);
            var models = new List<string>();
            if (payload.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        var value = id.GetString();
                        if (!string.IsNullOrWhiteSpace(value) && !models.Contains(value))
                        {
                            models.Add(value);
                        }
                    }
                }
            }

            if (models.Count == 0)
            {
                Fail("网关没有返回可用模型。");
                return;
            }

            PostToUi(new { type = "models-discovered", models });
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            Fail("模型发现失败：" + exception.Message);
        }
    }
}
