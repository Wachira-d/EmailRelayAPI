using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EmailRelayAPI.Options;
using EmailRelayAPI.Services;
using Xunit;

namespace EmailRelayAPI.Tests;

/// <summary>Guide section 4: the decision table.</summary>
public class AuthenApiClientTests
{
    private static AuthenResult Classify(int status, string body, string mediaType = "application/json", Action<HttpResponseMessage>? tweak = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        tweak?.Invoke(response);
        return AuthenApiClient.Classify(response, body);
    }

    private const string OkJson = """{"success":true,"message":"Authentication successful","data":{"samAccountName":"Phuriphat.P","userPrincipalName":"Phuriphat.Phiphatthanawaranon@ipsos.com","displayName":"Phuriphat","groups":["TH-IT"],"isEnabled":true,"isLocked":false}}""";

    [Fact]
    public void Ok200Json_IsSuccess_AndUserKeyIsLowerCaseUpn()
    {
        var r = Classify(200, OkJson);
        Assert.Equal(AuthenOutcome.Success, r.Outcome);
        Assert.Equal("phuriphat.phiphatthanawaranon@ipsos.com", r.User!.UserKey);
    }

    [Theory]
    [InlineData("", "Phuriphat.P", "phuriphat.p")]
    [InlineData("  ", "Phuriphat.P", "phuriphat.p")]
    [InlineData("", "", null)]
    [InlineData(null, "  ", null)]
    public void UserKey_FallsBackToSam_AndIsNullWhenBothBlank(string? upn, string? sam, string? expected)
        => Assert.Equal(expected, new AuthenUserInfo { UserPrincipalName = upn, SamAccountName = sam }.UserKey);

    [Fact]
    public void Ok200Html_IsIndeterminate() => Assert.Equal(AuthenOutcome.Indeterminate, Classify(200, "<html>proxy</html>", "text/html").Outcome);

    [Fact]
    public void Unauthorized401JsonWithoutReason_IsInvalidCredentials()
        => Assert.Equal(AuthenOutcome.InvalidCredentials, Classify(401, """{"success":false,"message":"Authentication failed"}""").Outcome);

    [Theory]
    [InlineData("Missing")]
    [InlineData("Invalid")]
    [InlineData("Expired")]
    [InlineData("Disabled")]
    [InlineData("Revoked")]
    public void Unauthorized401JsonWithReason_IsConfigurationError(string reason)
        => Assert.Equal(AuthenOutcome.ConfigurationError, Classify(401, $$"""{"success":false,"message":"API key","reason":"{{reason}}"}""").Outcome);

    [Fact]
    public void ApiKeyFailureAsSentByAuthenApi_IsConfigurationError_WithTheReason()
    {
        // AuthenAPI's ApiKeyAuthMiddleware writes {"error": ..., "reason": ...} without "success".
        var r = Classify(401, """{"error":"Invalid API key","reason":"Invalid"}""");
        Assert.Equal(AuthenOutcome.ConfigurationError, r.Outcome);
        Assert.Contains("reason: Invalid", r.Detail);
    }

    [Fact]
    public void JsonServedWithAnotherContentType_IsStillRead()
        => Assert.Equal(AuthenOutcome.Success, Classify(200, OkJson, "text/plain").Outcome);

    [Fact]
    public void FullAuthenApiUserPayload_IsRead()
    {
        const string body = """{"success":true,"message":"ok","data":{"distinguishedName":"CN=x","samAccountName":"Phuriphat.P","userPrincipalName":"Phuriphat.Phiphatthanawaranon@ipsos.com","displayName":"P","firstName":"","lastName":"","email":"","department":"IT","title":"","office":"","phone":"","mobile":"","manager":"","employeeId":"12345","company":"Ipsos","isEnabled":true,"isLocked":false,"groups":["TH-IT"],"groupDistinguishedNames":["CN=TH-IT,OU=G"],"whenCreated":"2020-01-01T00:00:00Z","whenChanged":null,"lastLogon":"2026-11-16T05:43:20Z"},"error":null,"timestamp":"2026-11-16T05:43:20Z"}""";
        var r = Classify(200, body);
        Assert.Equal(AuthenOutcome.Success, r.Outcome);
        Assert.Equal(new[] { "TH-IT" }, r.User!.Groups);
    }

    [Fact]
    public void BadRequestThatIsNotJson_IsIndeterminate()
        => Assert.Equal(AuthenOutcome.Indeterminate, Classify(400, "<html>Bad Request - Invalid Hostname</html>", "text/html").Outcome);

    [Fact]
    public void Unauthorized401Html_IsConfigurationError() => Assert.Equal(AuthenOutcome.ConfigurationError, Classify(401, "<html>401</html>", "text/html").Outcome);

    [Fact]
    public void BadRequest400_IsInvalidCredentials() => Assert.Equal(AuthenOutcome.InvalidCredentials, Classify(400, """{"success":false}""").Outcome);

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(405)]
    [InlineData(301)]
    [InlineData(302)]
    public void MisconfigurationStatuses_AreConfigurationError(int status)
        => Assert.Equal(AuthenOutcome.ConfigurationError, Classify(status, "<html/>", "text/html").Outcome);

    [Fact]
    public void TooMany429_IsRateLimited_WithRetryAfter()
    {
        var r = Classify(429, """{"success":false}""", tweak: m => m.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)));
        Assert.Equal(AuthenOutcome.RateLimited, r.Outcome);
        Assert.Equal(30, r.RetryAfterSeconds);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(504)]
    public void ServerErrorAndGatewayTimeout_AreIndeterminate(int status)
        => Assert.Equal(AuthenOutcome.Indeterminate, Classify(status, """{"success":false}""").Outcome);

    [Theory]
    [InlineData(502, "<html>bad gateway</html>", "text/html")]
    [InlineData(503, "<html>Service Unavailable</html>", "text/html")]
    [InlineData(503, """{"success":false,"message":"AD down"}""", "application/json")]
    public void NeverReachedOrAdDown_IsUnreachable(int status, string body, string mediaType)
        => Assert.Equal(AuthenOutcome.Unreachable, Classify(status, body, mediaType).Outcome);

    [Fact]
    public void Timeout_IsIndeterminate()
        => Assert.Equal(AuthenOutcome.Indeterminate, AuthenApiClient.ClassifyException(new TaskCanceledException("timeout", new TimeoutException())).Outcome);

    [Theory]
    [InlineData(HttpRequestError.ConnectionError, AuthenOutcome.Unreachable)]
    [InlineData(HttpRequestError.NameResolutionError, AuthenOutcome.Unreachable)]
    [InlineData(HttpRequestError.SecureConnectionError, AuthenOutcome.Unreachable)]
    [InlineData(HttpRequestError.ResponseEnded, AuthenOutcome.Indeterminate)]
    [InlineData(HttpRequestError.Unknown, AuthenOutcome.Indeterminate)]
    public void TransportErrors_AreClassifiedByPhase(HttpRequestError error, AuthenOutcome expected)
        => Assert.Equal(expected, AuthenApiClient.ClassifyException(new HttpRequestException(error, "x")).Outcome);

    // ---- guide test case 10: http base URL is refused at startup ----

    [Theory]
    [InlineData("http://authen.hondacx.me", false)]
    [InlineData("https://authen.hondacx.me/other", false)]
    [InlineData("authen.hondacx.me", false)]
    [InlineData("https://authen.hondacx.me", true)]
    [InlineData("https://authen.hondacx.me/", true)]
    [InlineData("https://authen.hondacx.me/api", true)]
    [InlineData("", true)]
    public void BaseUrl_MustBeHttpsWithoutPath(string url, bool valid)
        => Assert.Equal(valid, new AuthenApiOptions { BaseUrl = url }.Validate() == null);

    [Fact]
    public void LegacyApiSuffix_IsRemovedFromTheBaseUrl()
        => Assert.Equal("https://authen.hondacx.me", new AuthenApiOptions { BaseUrl = "https://authen.hondacx.me/api/" }.NormalizedBaseUrl);
}
