using System.Net;
using System.Net.Http;
using Common.Security;

namespace RemoteCli.UnitTests;

public sealed class RelayServerIdentityTests
{
    [Fact]
    public async Task ReadsHealthPayloadWithAdditionalServerFields()
    {
        using var client = new HttpClient(new StaticResponseHandler(
            "{\"status\":\"ready\",\"webSocketRelayAvailable\":true,\"serverFingerprintSha256\":\"DCED4BCFDB98516EA9C3F7E44FE0031CE413C43B9906CAC025DA66DF1EFF54D3\",\"adminInitialized\":false,\"officialClientsOnly\":false}"));

        var health = await RelayServerIdentityClient.ReadAsync(
            client,
            new Uri("https://relay.example/remotecli/"),
            CancellationToken.None);

        Assert.Equal("DCED4BCFDB98516EA9C3F7E44FE0031CE413C43B9906CAC025DA66DF1EFF54D3", health.ServerFingerprintSha256);
        Assert.False(health.AdminInitialized);
        Assert.False(health.OfficialClientsOnly);
    }

    private sealed class StaticResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://relay.example/remotecli/healthz", request.RequestUri?.AbsoluteUri);
            Assert.Equal(RelayClientIdentity.HeaderValue, request.Headers.GetValues(RelayClientIdentity.HeaderName).Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
