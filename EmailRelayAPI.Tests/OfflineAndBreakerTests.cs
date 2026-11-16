using EmailRelayAPI.Options;
using EmailRelayAPI.Security;
using EmailRelayAPI.Services;
using Xunit;

namespace EmailRelayAPI.Tests;

public class OfflineCredentialHasherTests
{
    [Fact]
    public void Verifies_OnlyWithTheSamePasswordAndPepper()
    {
        var hash = OfflineCredentialHasher.Hash("P@ssw0rd!", "pepper-A-0123456789012345678901234");
        Assert.StartsWith("v2$600000$", hash);
        Assert.True(OfflineCredentialHasher.Verify("P@ssw0rd!", "pepper-A-0123456789012345678901234", hash));
        Assert.False(OfflineCredentialHasher.Verify("P@ssw0rd", "pepper-A-0123456789012345678901234", hash));
        Assert.False(OfflineCredentialHasher.Verify("P@ssw0rd!", "pepper-B-0123456789012345678901234", hash));
    }

    [Fact]
    public void RejectsLegacyAndMalformedHashes()
    {
        Assert.False(OfflineCredentialHasher.Verify("x", "p", PasswordHasher.HashToString("x")));
        Assert.False(OfflineCredentialHasher.Verify("x", "p", "v2$1000$AAAA$AAAA"));
        Assert.False(OfflineCredentialHasher.Verify("x", "p", ""));
    }
}

/// <summary>Guide section 8.</summary>
public class CircuitBreakerTests
{
    [Fact]
    public async Task OpensAfterThreeUnreachable_ThenAllowsOneTrial_AndClosesOnAnswer()
    {
        var breaker = new AuthenCircuitBreaker(new TestOptions<AuthenApiOptions>(new AuthenApiOptions { CircuitFailureThreshold = 3, CircuitOpenSeconds = 1 }));
        for (var i = 0; i < 3; i++)
        {
            Assert.True(breaker.TryAcquire());
            breaker.Record(AuthenOutcome.Unreachable);
        }
        Assert.Equal("open", breaker.State);
        Assert.False(breaker.TryAcquire());

        await Task.Delay(1100);
        Assert.True(breaker.TryAcquire());   // the single half-open trial
        Assert.False(breaker.TryAcquire());  // everyone else still falls back
        breaker.Record(AuthenOutcome.InvalidCredentials); // AuthenAPI answered
        Assert.Equal("closed", breaker.State);
        Assert.True(breaker.TryAcquire());
    }

    [Theory]
    [InlineData(AuthenOutcome.Indeterminate)]
    [InlineData(AuthenOutcome.InvalidCredentials)]
    public void TimeoutsAnd401s_DoNotOpenTheCircuit(AuthenOutcome outcome)
    {
        var breaker = new AuthenCircuitBreaker(new TestOptions<AuthenApiOptions>(new AuthenApiOptions()));
        for (var i = 0; i < 10; i++) breaker.Record(outcome);
        Assert.Equal("closed", breaker.State);
    }
}
