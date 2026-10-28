using System.Net;
using BridgeBeats.Web.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

#pragma warning disable CS1591
namespace BridgeBeats.Tests.Unit;

/// <summary>Checks that forwarded login addresses are used only for the configured proxy.</summary>
[TestClass]
public class OAuthLoginClientAddressResolverTests {
    [TestMethod]
    public async Task TrustedProxy_UsesForwardedClientAddress( ) {
        using OAuthLoginClientAddressResolver resolver = Resolver( "192.0.2.10", Resolve( "192.0.2.10" ) );
        await resolver.RefreshAsync( CancellationToken.None );
        DefaultHttpContext context = NewContext( "192.0.2.10", "198.51.100.5" );

        Assert.AreEqual( "198.51.100.5", resolver.GetPartitionKey( context ) );
    }

    [TestMethod]
    public async Task UntrustedPeer_CannotChooseRateLimitBucket( ) {
        using OAuthLoginClientAddressResolver resolver = Resolver( "192.0.2.10", Resolve( "192.0.2.10" ) );
        await resolver.RefreshAsync( CancellationToken.None );
        DefaultHttpContext context = NewContext( "203.0.113.7", "198.51.100.5" );

        Assert.AreEqual( "203.0.113.7", resolver.GetPartitionKey( context ) );
    }

    [TestMethod]
    public async Task TrustedProxy_RejectsAmbiguousForwardingChain( ) {
        using OAuthLoginClientAddressResolver resolver = Resolver( "192.0.2.10", Resolve( "192.0.2.10" ) );
        await resolver.RefreshAsync( CancellationToken.None );
        DefaultHttpContext context = NewContext( "192.0.2.10", "198.51.100.5, 203.0.113.7" );

        Assert.AreEqual( "192.0.2.10", resolver.GetPartitionKey( context ) );
    }

    [TestMethod]
    public void NoTrustedProxy_IgnoresForwardedAddress( ) {
        using OAuthLoginClientAddressResolver resolver = new( null );
        DefaultHttpContext context = NewContext( "192.0.2.10", "198.51.100.5" );

        Assert.AreEqual( "192.0.2.10", resolver.GetPartitionKey( context ) );
    }

    /// <summary>
    /// A lookup failure the old <c>SocketException</c>-only catch did not cover must not propagate.
    /// The refresh loop is a hosted service, so an escaping exception stops the whole application.
    /// </summary>
    [TestMethod]
    public async Task LookupFailure_DoesNotPropagateAndFailsClosed( ) {
        CapturingLoggerFactory loggerFactory = new( );
        using OAuthLoginClientAddressResolver resolver = Resolver(
            "192.0.2.10",
            ( _, _ ) => throw new InvalidOperationException( "name resolution is unavailable" ),
            loggerFactory );
        DefaultHttpContext context = NewContext( "192.0.2.10", "198.51.100.5" );

        await resolver.RefreshAsync( CancellationToken.None );

        Assert.AreEqual( "192.0.2.10", resolver.GetPartitionKey( context ),
            "an unresolved proxy must not be trusted to choose a rate-limit bucket" );
        Assert.Contains( entry => entry.Level == LogLevel.Warning, loggerFactory.Entries,
            "a failed proxy lookup must be reported" );
    }

    /// <summary>
    /// The lookup-failure warning must not carry the proxy host name or the resolver's exception.
    /// Host names reveal the deployment's private network topology, and resolver exceptions can echo
    /// it back in their messages, so neither belongs in application logs.
    /// </summary>
    [TestMethod]
    public async Task LookupFailure_WarningOmitsProxyHostAndException( ) {
        const string ProxyHost = "proxy.internal.example.invalid";
        CapturingLoggerFactory loggerFactory = new( );
        using OAuthLoginClientAddressResolver resolver = Resolver(
            ProxyHost,
            ( _, _ ) => throw new InvalidOperationException( $"name resolution is unavailable for {ProxyHost}" ),
            loggerFactory );
        DefaultHttpContext context = NewContext( "192.0.2.10", "198.51.100.5" );

        await resolver.RefreshAsync( CancellationToken.None );

        // Behavior is unchanged: the failure still fails closed.
        Assert.AreEqual( "192.0.2.10", resolver.GetPartitionKey( context ),
            "an unresolved proxy must not be trusted to choose a rate-limit bucket" );

        CapturedLogEntry warning = loggerFactory.Entries.Single( entry => entry.Level == LogLevel.Warning );
        Assert.IsFalse( warning.Message.Contains( ProxyHost, StringComparison.Ordinal ),
            $"the warning must not name the proxy host: {warning.Message}" );
        Assert.IsNull( warning.Exception, "the warning must not attach the resolver's exception" );
    }

    /// <summary>A resolved proxy must lose its trust when a later lookup fails.</summary>
    [TestMethod]
    public async Task LookupFailure_ClearsPreviouslyTrustedAddress( ) {
        bool fail = false;
        using OAuthLoginClientAddressResolver resolver = Resolver( "192.0.2.10", ( host, _ ) => {
            if (fail) { throw new InvalidOperationException( "name resolution is unavailable" ); }
            return Task.FromResult( ToAddresses( [host] ) );
        } );
        DefaultHttpContext context = NewContext( "192.0.2.10", "198.51.100.5" );

        await resolver.RefreshAsync( CancellationToken.None );
        Assert.AreEqual( "198.51.100.5", resolver.GetPartitionKey( context ) );

        fail = true;
        await resolver.RefreshAsync( CancellationToken.None );

        Assert.AreEqual( "192.0.2.10", resolver.GetPartitionKey( context ) );
    }

    /// <summary>A transient failure must not latch forwarded addresses off permanently.</summary>
    [TestMethod]
    public async Task LookupFailure_RecoversOnNextSuccessfulRefresh( ) {
        bool fail = true;
        using OAuthLoginClientAddressResolver resolver = Resolver( "192.0.2.10", ( host, _ ) => {
            if (fail) { throw new InvalidOperationException( "name resolution is unavailable" ); }
            return Task.FromResult( ToAddresses( [host] ) );
        } );
        DefaultHttpContext context = NewContext( "192.0.2.10", "198.51.100.5" );

        await resolver.RefreshAsync( CancellationToken.None );
        Assert.AreEqual( "192.0.2.10", resolver.GetPartitionKey( context ) );

        fail = false;
        await resolver.RefreshAsync( CancellationToken.None );

        Assert.AreEqual( "198.51.100.5", resolver.GetPartitionKey( context ) );
    }

    /// <summary>
    /// Shutdown cancellation is expected control flow, not a lookup failure: it must escape
    /// <c>RefreshAsync</c> so the host stops instead of logging an error and looping.
    /// </summary>
    [TestMethod]
    public async Task CancelledLookup_PropagatesCancellation( ) {
        CapturingLoggerFactory loggerFactory = new( );
        using CancellationTokenSource stopping = new( );
        await stopping.CancelAsync( );
        using OAuthLoginClientAddressResolver resolver = Resolver(
            "192.0.2.10",
            ( _, token ) => Task.FromCanceled<IPAddress[]>( token ),
            loggerFactory );

        _ = await Assert.ThrowsAsync<OperationCanceledException>( ( ) => resolver.RefreshAsync( stopping.Token ) );
        Assert.IsEmpty( loggerFactory.Entries, "shutdown cancellation must not be reported as a lookup failure" );
    }

    /// <summary>A lookup that returns no usable address must not leave stale trust in place.</summary>
    [TestMethod]
    public async Task LookupReturningNoAddresses_FailsClosed( ) {
        using OAuthLoginClientAddressResolver resolver = Resolver( "192.0.2.10", ( _, _ ) => Task.FromResult( Array.Empty<IPAddress>( ) ) );
        await resolver.RefreshAsync( CancellationToken.None );
        DefaultHttpContext context = NewContext( "192.0.2.10", "198.51.100.5" );

        Assert.AreEqual( "192.0.2.10", resolver.GetPartitionKey( context ) );
    }

    private static OAuthLoginClientAddressResolver Resolver(
        string? trustedProxyHost,
        Func<string, CancellationToken, Task<IPAddress[]>> resolveHost,
        ILoggerFactory? loggerFactory = null
    ) => new( trustedProxyHost, resolveHost, loggerFactory );

    private static Func<string, CancellationToken, Task<IPAddress[]>> Resolve( params string[] addresses ) =>
        ( _, _ ) => Task.FromResult( ToAddresses( addresses ) );

    private static IPAddress[] ToAddresses( string[] addresses ) => Array.ConvertAll( addresses, IPAddress.Parse );

    private static DefaultHttpContext NewContext( string peerAddress, string forwardedAddress ) {
        DefaultHttpContext context = new( );
        context.Connection.RemoteIpAddress = IPAddress.Parse( peerAddress );
        context.Request.Headers["X-Forwarded-For"] = forwardedAddress;
        return context;
    }

    private sealed record CapturedLogEntry( LogLevel Level, string Message, Exception? Exception );

    private sealed class CapturingLoggerFactory : ILoggerFactory {
        internal List<CapturedLogEntry> Entries { get; } = [];

        public void AddProvider( ILoggerProvider provider ) { }

        public ILogger CreateLogger( string categoryName ) => new CapturingLogger( Entries );

        public void Dispose( ) { }

        private sealed class CapturingLogger( List<CapturedLogEntry> entries ) : ILogger {
            public IDisposable? BeginScope<TState>( TState state ) where TState : notnull => null;

            public bool IsEnabled( LogLevel logLevel ) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) => entries.Add( new CapturedLogEntry( logLevel, formatter( state, exception ), exception ) );
        }
    }
}
#pragma warning restore CS1591
