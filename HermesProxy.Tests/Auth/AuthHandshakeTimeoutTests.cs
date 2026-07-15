using System.Threading.Tasks;
using HermesProxy.Auth;
using Xunit;

namespace HermesProxy.Tests.Auth;

/// <summary>
/// JimsProxy (clean handshake teardown): <see cref="AuthClient.AwaitHandshakeResult"/> must bound
/// the realmd handshake wait. A realmd that accepts the TCP connection but never completes the
/// handshake must fail the login cleanly (a bounded wait → FAIL) instead of hanging the login
/// thread forever, while a handshake that completes in time must pass its real result through
/// unchanged (a genuine bad-password must NOT be masked as a timeout).
/// </summary>
public class AuthHandshakeTimeoutTests
{
    // Realmd accepted TCP but never sent LOGON_CHALLENGE -> the wait must bound and fail cleanly.
    [Fact]
    public void NeverCompletes_TimesOutCleanly()
    {
        var handshake = new TaskCompletionSource<AuthResult>(); // never set

        AuthResult result = AuthClient.AwaitHandshakeResult(handshake.Task, timeoutMs: 100, out bool timedOut);

        Assert.True(timedOut);
        Assert.Equal(AuthResult.FAIL_INTERNAL_ERROR, result);
    }

    // Handshake succeeded within the window -> return the real success result, not a timeout.
    [Fact]
    public void CompletesInTime_ReturnsRealResult()
    {
        var handshake = new TaskCompletionSource<AuthResult>();
        handshake.SetResult(AuthResult.SUCCESS);

        AuthResult result = AuthClient.AwaitHandshakeResult(handshake.Task, timeoutMs: 1000, out bool timedOut);

        Assert.False(timedOut);
        Assert.Equal(AuthResult.SUCCESS, result);
    }

    // A real auth failure that arrives in time must be propagated as-is, never reported as a timeout.
    [Fact]
    public void CompletesWithFailure_PropagatesThatFailure()
    {
        var handshake = new TaskCompletionSource<AuthResult>();
        handshake.SetResult(AuthResult.FAIL_INCORRECT_PASSWORD);

        AuthResult result = AuthClient.AwaitHandshakeResult(handshake.Task, timeoutMs: 1000, out bool timedOut);

        Assert.False(timedOut);
        Assert.Equal(AuthResult.FAIL_INCORRECT_PASSWORD, result);
    }
}
