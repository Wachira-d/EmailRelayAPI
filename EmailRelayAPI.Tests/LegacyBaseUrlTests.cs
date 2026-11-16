using System.Net;
using System.Text;
using EmailRelayAPI.Options;
using EmailRelayAPI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmailRelayAPI.Tests;

/// <summary>
/// Servers configured for the old login code have AuthenApi:BaseUrl ending in "/api" (the old code appended
/// "/ldap/authenticate"). Those settings must keep working; http and other paths stay refused.
/// </summary>
public class LegacyBaseUrlTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var body = """{"success":false,"message":"Authentication failed"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public Factory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    [Theory]
    [InlineData("https://authen.hondacx.me", "https://authen.hondacx.me/api/ldap/authenticate")]
    [InlineData("https://authen.hondacx.me/", "https://authen.hondacx.me/api/ldap/authenticate")]
    [InlineData("https://authen.hondacx.me/api", "https://authen.hondacx.me/api/ldap/authenticate")]
    [InlineData("https://authen.hondacx.me/api/", "https://authen.hondacx.me/api/ldap/authenticate")]
    [InlineData("https://authen.hondacx.me/API", "https://authen.hondacx.me/api/ldap/authenticate")]
    [InlineData("  https://authen.hondacx.me/api  ", "https://authen.hondacx.me/api/ldap/authenticate")]
    [InlineData("https://10.1.2.3:8443/api", "https://10.1.2.3:8443/api/ldap/authenticate")]
    public async Task LegacyAndCurrentForms_CallTheSameEndpoint(string baseUrl, string expected)
    {
        var options = new AuthenApiOptions { BaseUrl = baseUrl, ApiKey = "k" };
        Assert.Null(options.Validate());

        var handler = new CapturingHandler();
        var client = new AuthenApiClient(new Factory(handler), new TestOptions<AuthenApiOptions>(options), NullLogger<AuthenApiClient>.Instance);
        var result = await client.AuthenticateAsync("user@ipsos.com", "pw");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(expected, request.RequestUri!.ToString());
        Assert.Equal(AuthenOutcome.InvalidCredentials, result.Outcome); // reached AuthenAPI; the 401 is read normally
    }

    [Theory]
    [InlineData("http://authen.hondacx.me/api")]
    [InlineData("http://authen.hondacx.me")]
    [InlineData("https://authen.hondacx.me/authen/api")]
    [InlineData("https://authen.hondacx.me/api/ldap")]
    [InlineData("https://authen.hondacx.me/api?x=1")]
    public void HttpAndOtherPaths_AreStillRefused(string baseUrl)
        => Assert.NotNull(new AuthenApiOptions { BaseUrl = baseUrl }.Validate());
}
