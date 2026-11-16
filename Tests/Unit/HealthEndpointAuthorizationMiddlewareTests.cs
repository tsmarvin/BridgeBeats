using System.Net;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS1591

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="HealthEndpointAuthorizationMiddleware"/> covering IP-normalisation
/// correctness. Exercises the three negative controls required by the Phase-3 P1 §3.2 item-8 fix:
/// an IPv4-mapped-IPv6 internal address is admitted, a public address is rejected, and an
/// <c>X-Forwarded-For</c> header cannot override <c>RemoteIpAddress</c> (no spoofing bypass).
/// </summary>
[TestClass]
public class HealthEndpointAuthorizationMiddlewareTests {

    /// <summary>MSTest-injected context, used for per-test cancellation tokens.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Builds an <see cref="HttpContext"/> whose <c>Connection.RemoteIpAddress</c> is set to
    /// <paramref name="remoteIp"/> and whose request targets the liveness path.
    /// </summary>
    private static DefaultHttpContext BuildAliveContext( IPAddress remoteIp ) {
        DefaultHttpContext ctx = new( );
        ctx.Request.Path = EndpointPaths.Alive;
        ctx.Connection.RemoteIpAddress = remoteIp;
        return ctx;
    }

    /// <summary>
    /// Builds an <see cref="HttpContext"/> targeting the liveness path with <paramref name="remoteIp"/>
    /// as the connection address and an additional <c>X-Forwarded-For</c> header set to
    /// <paramref name="forwardedForValue"/>.
    /// </summary>
    private static DefaultHttpContext BuildAliveContextWithForwardedFor(
        IPAddress remoteIp,
        string forwardedForValue
    ) {
        DefaultHttpContext ctx = BuildAliveContext( remoteIp );
        ctx.Request.Headers["X-Forwarded-For"] = forwardedForValue;
        return ctx;
    }

    /// <summary>
    /// Invokes <see cref="HealthEndpointAuthorizationMiddleware"/> against <paramref name="ctx"/> and
    /// returns whether the downstream delegate ran and the final HTTP status code.
    /// </summary>
    private static async Task<(bool NextCalled, int StatusCode)> InvokeAsync( DefaultHttpContext ctx ) {
        bool nextCalled = false;
        RequestDelegate next = _ => {
            nextCalled = true;
            return Task.CompletedTask;
        };

        HealthEndpointAuthorizationMiddleware mw = new(
            next,
            NullLogger<HealthEndpointAuthorizationMiddleware>.Instance
        );

        await mw.InvokeAsync( ctx );
        return (nextCalled, ctx.Response.StatusCode);
    }

    /// <summary>
    /// An IPv4-mapped-IPv6 address whose embedded IPv4 part falls in the 10/8 private range
    /// (<c>::ffff:10.0.0.5</c>) must be classified as internal and allowed through the liveness gate.
    /// Failure-first: this test fails on the unfixed middleware (the AddressFamily guard rejects all
    /// non-loopback IPv6, returning 403) and passes after the normalisation fix.
    /// </summary>
    [TestMethod]
    public async Task Alive_IPv4MappedInternalAddress_Passes( ) {
        // Arrange
        DefaultHttpContext ctx = BuildAliveContext( IPAddress.Parse( "::ffff:10.0.0.5" ) );

        // Act
        (bool nextCalled, int statusCode) = await InvokeAsync( ctx );

        // Assert
        Assert.IsTrue( nextCalled, "Downstream must be called for an internal IPv4-mapped address." );
        Assert.AreEqual( 200, statusCode );
    }

    /// <summary>
    /// A public IP address (<c>203.0.113.1</c>, TEST-NET-3 per RFC 5737) must be rejected with
    /// HTTP 403. Regression guard: confirms the existing external-rejection path is preserved by the
    /// normalisation change. This test passes on both the unfixed and fixed middleware; the discipline
    /// applied is explicit behavioral assertion of the unchanged rejection path.
    /// </summary>
    [TestMethod]
    public async Task Alive_PublicAddress_Returns403( ) {
        // Arrange — 203.0.113.1 is documentation/test-net; not in any private range
        DefaultHttpContext ctx = BuildAliveContext( IPAddress.Parse( "203.0.113.1" ) );

        // Act
        (bool nextCalled, int statusCode) = await InvokeAsync( ctx );

        // Assert
        Assert.IsFalse( nextCalled, "Downstream must not be called for a public IP." );
        Assert.AreEqual( 403, statusCode );
    }

    /// <summary>
    /// Negative control (item-8): an IPv4-mapped IPv6 address whose embedded IPv4 part is a PUBLIC
    /// address (<c>::ffff:8.8.8.8</c>) must be rejected with HTTP 403 and must not invoke
    /// the downstream delegate. This proves the IPv4-mapped normalisation did not inadvertently
    /// widen the internal range — only embedded private IPv4 addresses are admitted.
    /// Failure-first: before the normalisation fix the middleware rejected ALL non-loopback IPv6
    /// (including mapped-internal ones) with 403, so the positive case failed, but this negative
    /// case passed. After the fix, mapped-internal passes and mapped-public is still rejected by
    /// the private-range check. The test was written first against pre-fix code to confirm 403,
    /// then verified it continues to return 403 on fixed code (same surface, unchanged rejection).
    /// </summary>
    [TestMethod]
    public async Task Alive_IPv4MappedPublicAddress_Returns403( ) {
        // Arrange — ::ffff:8.8.8.8 embeds the public Google DNS address; not in any private range
        DefaultHttpContext ctx = BuildAliveContext( IPAddress.Parse( "::ffff:8.8.8.8" ) );

        // Act
        (bool nextCalled, int statusCode) = await InvokeAsync( ctx );

        // Assert
        Assert.IsFalse( nextCalled, "Downstream must not be called for an IPv4-mapped public address." );
        Assert.AreEqual( 403, statusCode );
    }

    /// <summary>
    /// Boundary negative control: <c>::ffff:172.15.0.1</c> embeds an address one below the
    /// 172.16.0.0/12 private block and must be rejected with HTTP 403.
    /// </summary>
    [TestMethod]
    public async Task Alive_IPv4MappedJustBelowPrivate172Block_Returns403( ) {
        // Arrange — 172.15.0.1 is just outside the 172.16–31 private range (below)
        DefaultHttpContext ctx = BuildAliveContext( IPAddress.Parse( "::ffff:172.15.0.1" ) );

        // Act
        (bool nextCalled, int statusCode) = await InvokeAsync( ctx );

        // Assert
        Assert.IsFalse( nextCalled, "Downstream must not be called for 172.15.0.1 (not in 172.16/12 block)." );
        Assert.AreEqual( 403, statusCode );
    }

    /// <summary>
    /// Boundary negative control: <c>::ffff:172.32.0.1</c> embeds an address one above the
    /// 172.16.0.0/12 private block and must be rejected with HTTP 403.
    /// </summary>
    [TestMethod]
    public async Task Alive_IPv4MappedJustAbovePrivate172Block_Returns403( ) {
        // Arrange — 172.32.0.1 is just outside the 172.16–31 private range (above)
        DefaultHttpContext ctx = BuildAliveContext( IPAddress.Parse( "::ffff:172.32.0.1" ) );

        // Act
        (bool nextCalled, int statusCode) = await InvokeAsync( ctx );

        // Assert
        Assert.IsFalse( nextCalled, "Downstream must not be called for 172.32.0.1 (not in 172.16/12 block)." );
        Assert.AreEqual( 403, statusCode );
    }

    /// <summary>
    /// A request from a public <c>RemoteIpAddress</c> carrying a spoofed
    /// <c>X-Forwarded-For: 10.0.0.1</c> header must still be rejected with HTTP 403. Confirms that
    /// the middleware reads only <c>Connection.RemoteIpAddress</c> and does NOT honour forwarded
    /// headers. Regression guard: this test passes on both the unfixed and fixed middleware; the
    /// discipline applied is explicit behavioral assertion that the forwarded-header path is absent.
    /// </summary>
    [TestMethod]
    public async Task Alive_SpoofedXForwardedFor_Returns403( ) {
        // Arrange — public RemoteIpAddress with an internal X-Forwarded-For header
        DefaultHttpContext ctx = BuildAliveContextWithForwardedFor(
            IPAddress.Parse( "203.0.113.1" ),
            "10.0.0.1"
        );

        // Act
        (bool nextCalled, int statusCode) = await InvokeAsync( ctx );

        // Assert
        Assert.IsFalse( nextCalled, "X-Forwarded-For must not override RemoteIpAddress." );
        Assert.AreEqual( 403, statusCode );
    }
}
