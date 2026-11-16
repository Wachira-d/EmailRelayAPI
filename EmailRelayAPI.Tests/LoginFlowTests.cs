using EmailRelayAPI.Security;
using EmailRelayAPI.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmailRelayAPI.Tests;

/// <summary>The guide's pre-production test cases (section 10) and rules (sections 1, 4, 6, 7).</summary>
public class LoginFlowTests
{
    private const string Upn = "Phuriphat.Phiphatthanawaranon@ipsos.com";
    private const string Key = "phuriphat.phiphatthanawaranon@ipsos.com";
    private const string Password = "Correct-Horse-1";

    private static void AnswerViewer(LoginHarness h)
        => h.Api.Handler = (_, pw) => pw == Password ? LoginHarness.Ok(Upn, "Phuriphat.P", "TH-IT") : LoginHarness.Result(AuthenOutcome.InvalidCredentials, 401);

    // 1 + 2: every typed form maps to the same user key; the typed text is sent unchanged.
    [Fact]
    public async Task Case1and2_AllNameFormsGiveTheSameUserKey_AndAreSentAsTyped()
    {
        using var h = new LoginHarness();
        AnswerViewer(h);

        foreach (var typed in new[] { "  " + Upn + " ", @"IPSOSGROUP\Phuriphat.P", "Phuriphat.P" })
        {
            var r = await h.Login(typed, Password);
            Assert.True(r.Success, r.Error);
            Assert.Equal(Key, r.User!.Username);
            Assert.False(r.Degraded);
        }

        Assert.Equal(new[] { Upn, @"IPSOSGROUP\Phuriphat.P", "Phuriphat.P" }, h.Api.SentUsernames);
        Assert.Equal(1, h.Db(db => db.AdminUsers.Count()));
        Assert.Equal(3, h.Db(db => db.AdminUserAliases.Count(a => a.AdminUser.Username == Key)));
    }

    // 3: wrong password → one AuthenAPI call, no AD bind, generic message.
    [Fact]
    public async Task Case3_WrongPassword_CallsAuthenApiOnce_AndNeverFallsBack()
    {
        using var h = new LoginHarness();
        AnswerViewer(h);
        h.DirectAd.IsEnabled = true;

        var r = await h.Login(Upn, "wrong");

        Assert.Equal(LoginFailure.InvalidCredentials, r.Failure);
        Assert.Equal(AdminAuthService.MsgInvalid, r.Error);
        Assert.Equal(1, h.Api.Calls);
        Assert.Equal(0, h.DirectAd.Calls);
    }

    // 4: API key rejected → "contact the administrator", never "wrong password", and an alert.
    [Fact]
    public async Task Case4_ApiKeyProblem_IsAConfigurationError()
    {
        using var h = new LoginHarness();
        h.Api.Handler = (_, _) => LoginHarness.Result(AuthenOutcome.ConfigurationError, 401);

        var r = await h.Login(Upn, Password);

        Assert.Equal(LoginFailure.ConfigurationError, r.Failure);
        Assert.NotEqual(AdminAuthService.MsgInvalid, r.Error);
        Assert.Contains("AuthenApiConfigurationError", h.Alerts.Raised);
    }

    // 5 + 6: AuthenAPI unreachable → direct AD (if any) → offline cache within 24 h → degraded read-only session.
    [Fact]
    public async Task Case5and6_Unreachable_UsesOfflineCache_AsDegradedReadOnlySession()
    {
        using var h = new LoginHarness();
        AnswerViewer(h);
        Assert.True((await h.Login(Upn, Password)).Success);
        Assert.True(h.Db(db => db.AdminUsers.Single().PasswordHash.StartsWith("v2$")));

        h.Api.Handler = (_, _) => LoginHarness.Result(AuthenOutcome.Unreachable);
        h.DirectAd.IsEnabled = true; // configured but the DC is also down → step 3

        var r = await h.Login(Upn.ToUpperInvariant(), Password); // same name, different case: same alias

        Assert.True(r.Success, r.Error);
        Assert.True(r.Degraded);
        Assert.Equal(AdminAuthService.ModeOffline, r.Mode);
        Assert.Equal(RelayClaims.RoleViewer, r.Role);
        Assert.Equal(new[] { Upn }, h.DirectAd.BoundAs); // bound once, with the UPN from the cache
    }

    [Fact]
    public async Task Case5_DirectAdSuccess_GivesDegradedSession()
    {
        using var h = new LoginHarness();
        AnswerViewer(h);
        await h.Login(Upn, Password);
        h.Api.Handler = (_, _) => LoginHarness.Result(AuthenOutcome.Unreachable);
        h.DirectAd.IsEnabled = true;
        h.DirectAd.Handler = (_, pw) => pw == Password ? new DirectAdResult(DirectAdOutcome.Success, "ok") : new DirectAdResult(DirectAdOutcome.InvalidCredentials, "52e");

        var r = await h.Login(Upn, Password);
        Assert.True(r.Success);
        Assert.Equal(AdminAuthService.ModeDirectAd, r.Mode);
        Assert.True(r.Degraded);

        var wrong = await h.Login(Upn, "wrong");
        Assert.Equal(LoginFailure.InvalidCredentials, wrong.Failure);
        Assert.False(h.Db(db => db.AdminUsers.Single().PasswordHash.StartsWith("v2$"))); // cache removed after AD said 49
    }

    // 7: cache older than the window, or a name never used → "unavailable", not "wrong password".
    [Fact]
    public async Task Case7_ExpiredCacheOrUnknownUser_IsUnavailable()
    {
        using var h = new LoginHarness();
        AnswerViewer(h);
        await h.Login(Upn, Password);
        h.Db(db => db.AdminUsers.ExecuteUpdate(s => s.SetProperty(u => u.LastAdSync, DateTime.UtcNow.AddHours(-25))));
        h.Api.Handler = (_, _) => LoginHarness.Result(AuthenOutcome.Unreachable);

        var expired = await h.Login(Upn, Password);
        Assert.Equal(LoginFailure.Unavailable, expired.Failure);

        var unknown = await h.Login("someone.else@ipsos.com", Password);
        Assert.Equal(LoginFailure.Unavailable, unknown.Failure);
        Assert.NotEqual(AdminAuthService.MsgInvalid, unknown.Error);
    }

    // 8: timeout / 500 → "unavailable"; no AD bind and no cache, even with a cached user.
    [Fact]
    public async Task Case8_Indeterminate_NeverFallsBack()
    {
        using var h = new LoginHarness();
        AnswerViewer(h);
        await h.Login(Upn, Password);
        h.Api.Handler = (_, _) => LoginHarness.Result(AuthenOutcome.Indeterminate);
        h.DirectAd.IsEnabled = true;

        var r = await h.Login(Upn, "wrong-password");

        Assert.Equal(LoginFailure.Unavailable, r.Failure);
        Assert.Equal(0, h.DirectAd.Calls);
        Assert.Equal(0, h.Db(db => db.AdminUsers.Single().OfflineFailures));
    }

    // 9: 12 concurrent wrong offline passwords → at most 5 are checked.
    [Fact]
    public async Task Case9_ConcurrentOfflineGuesses_AreCappedAtFive()
    {
        using var h = new LoginHarness(o => o.MaxFailedAttempts = 100);
        AnswerViewer(h);
        await h.Login(Upn, Password);
        h.Api.Handler = (_, _) => LoginHarness.Result(AuthenOutcome.Unreachable);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => h.Login(Upn, "guess-" + i, "10.0.0." + i))));

        Assert.True(results.Count(r => r.Failure == LoginFailure.InvalidCredentials) <= 5);
        Assert.Equal(5, h.Db(db => db.AdminUsers.Single().OfflineFailures));

        var correct = await h.Login(Upn, Password, "10.9.9.9");
        Assert.Equal(LoginFailure.Unavailable, correct.Failure); // exhausted until the next online login
    }

    // Rule 4 / section 6: a 401 removes this person's offline credential.
    [Fact]
    public async Task Online401_RemovesTheOfflineCredential()
    {
        using var h = new LoginHarness();
        AnswerViewer(h);
        await h.Login(Upn, Password);

        await h.Login(Upn, "old-password");

        Assert.Equal("", h.Db(db => db.AdminUsers.Single().PasswordHash));
    }

    // Section 6: a name never typed before has no cache entry, so it cannot be used offline.
    [Fact]
    public async Task NameNeverTypedBefore_CannotUseTheOfflineCache()
    {
        using var h = new LoginHarness();
        AnswerViewer(h);
        await h.Login(Upn, Password);
        h.Api.Handler = (_, _) => LoginHarness.Result(AuthenOutcome.Unreachable);

        var r = await h.Login("Phuriphat.P", Password);

        Assert.Equal(LoginFailure.Unavailable, r.Failure);
    }

    // Section 6: admin accounts are never cached offline.
    [Fact]
    public async Task AdminAccounts_HaveNoOfflineCredential()
    {
        using var h = new LoginHarness();
        h.Api.Handler = (_, _) => LoginHarness.Ok(Upn, "Phuriphat.P", "TH-RelayAdmins");

        var online = await h.Login(Upn, Password);
        Assert.Equal(RelayClaims.RoleAdmin, online.Role);
        Assert.Equal("", h.Db(db => db.AdminUsers.Single().PasswordHash));

        h.Api.Handler = (_, _) => LoginHarness.Result(AuthenOutcome.Unreachable);
        Assert.Equal(LoginFailure.Unavailable, (await h.Login(Upn, Password)).Failure);
    }

    [Fact]
    public async Task OfflineCacheDisabled_WithoutPepper()
    {
        using var h = new LoginHarness(o => o.OfflineCachePepper = "");
        AnswerViewer(h);
        await h.Login(Upn, Password);
        Assert.Equal("", h.Db(db => db.AdminUsers.Single().PasswordHash));
    }

    // Section 9: local throttle per username, without calling AuthenAPI once exceeded.
    [Fact]
    public async Task FiveFailures_ThrottleTheUsername_WithoutCallingAuthenApi()
    {
        using var h = new LoginHarness();
        AnswerViewer(h);
        for (var i = 0; i < 5; i++) await h.Login(Upn, "wrong", "10.0.1." + i);
        var calls = h.Api.Calls;

        var sixth = await h.Login(Upn, Password, "10.0.2.1");

        Assert.Equal(LoginFailure.Throttled, sixth.Failure);
        Assert.Equal(calls, h.Api.Calls);
    }

    [Fact]
    public async Task UserOutsideMappedGroups_IsNotAuthorized()
    {
        using var h = new LoginHarness();
        h.Api.Handler = (_, _) => LoginHarness.Ok(Upn, "Phuriphat.P", "Some-Other-Group");
        Assert.Equal(LoginFailure.NotAuthorized, (await h.Login(Upn, Password)).Failure);
    }

    [Fact]
    public async Task DisabledConsoleUser_CannotSignIn_AndLosesTheOfflineCredential()
    {
        using var h = new LoginHarness();
        AnswerViewer(h);
        await h.Login(Upn, Password);
        await h.Service.SetEnabledAsync(Key, false, "tester");

        Assert.Equal(LoginFailure.NotAuthorized, (await h.Login(Upn, Password)).Failure);
        Assert.False(await h.Service.IsUserActiveAsync(Key));
        Assert.Equal("", h.Db(db => db.AdminUsers.Single().PasswordHash));
    }

    [Fact]
    public async Task UserWithoutUpnOrSam_IsRefused()
    {
        using var h = new LoginHarness();
        h.Api.Handler = (_, _) => LoginHarness.Ok("", "", "TH-IT");
        Assert.Equal(LoginFailure.ConfigurationError, (await h.Login(Upn, Password)).Failure);
        Assert.Equal(0, h.Db(db => db.AdminUsers.Count()));
    }

    [Fact]
    public async Task InvalidUsernameCharacters_AreNotSentToAuthenApi()
    {
        using var h = new LoginHarness();
        AnswerViewer(h);
        Assert.Equal(LoginFailure.InvalidCredentials, (await h.Login("bob'; drop", Password)).Failure);
        Assert.Equal(0, h.Api.Calls);
    }

    [Fact]
    public async Task OpenCircuit_SkipsAuthenApi_AndUsesTheFallback()
    {
        using var h = new LoginHarness();
        AnswerViewer(h);
        await h.Login(Upn, Password);
        h.Api.Handler = (_, _) => LoginHarness.Result(AuthenOutcome.Unreachable);
        for (var i = 0; i < 3; i++) await h.Login(Upn, Password);
        var calls = h.Api.Calls;

        var r = await h.Login(Upn, Password);

        Assert.True(r.Degraded);
        Assert.Equal(calls, h.Api.Calls);
    }
}
