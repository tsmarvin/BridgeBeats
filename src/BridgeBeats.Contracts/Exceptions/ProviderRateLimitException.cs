using BridgeBeats.Contracts.Enums;

namespace BridgeBeats.Contracts.Exceptions;

/// <summary>Base exception for a provider rate-limit response that carries a retry delay.</summary>
public class ProviderRateLimitException : Exception {
    /// <summary>The nonnegative delay before the provider may be retried.</summary>
    public TimeSpan RetryAfterValue { get; }

    /// <summary>The request URI that was rate-limited, when known.</summary>
    public Uri? RequestUri { get; }

    /// <summary>The provider that issued the limit, when known.</summary>
    public SupportedProviders? Provider { get; }

    /// <summary>The stable provider endpoint key that was rate-limited, when known.</summary>
    public string? Endpoint { get; }

    /// <summary>Creates a provider rate-limit exception.</summary>
    public ProviderRateLimitException(
        TimeSpan retryAfterValue,
        Uri? requestUri,
        SupportedProviders? provider,
        string? endpoint = null
    ) : base( BuildMessage( retryAfterValue, requestUri, provider, endpoint ) ) {
        RetryAfterValue = ValidateRetryAfter( retryAfterValue );
        RequestUri = requestUri;
        Provider = provider;
        Endpoint = endpoint;
    }

    /// <summary>Creates a rate-limit exception with a caller-supplied diagnostic message.</summary>
    protected ProviderRateLimitException(
        string message,
        TimeSpan retryAfterValue,
        Uri? requestUri,
        SupportedProviders? provider,
        string? endpoint = null
    ) : base( message ) {
        RetryAfterValue = ValidateRetryAfter( retryAfterValue );
        RequestUri = requestUri;
        Provider = provider;
        Endpoint = endpoint;
    }

    /// <summary>Creates a rate-limit exception with a caller-supplied message and inner cause.</summary>
    protected ProviderRateLimitException(
        string message,
        TimeSpan retryAfterValue,
        Uri? requestUri,
        SupportedProviders? provider,
        Exception innerException,
        string? endpoint = null
    ) : base( message, innerException ) {
        RetryAfterValue = ValidateRetryAfter( retryAfterValue );
        RequestUri = requestUri;
        Provider = provider;
        Endpoint = endpoint;
    }

    /// <summary>Creates a provider rate-limit exception with an inner cause.</summary>
    public ProviderRateLimitException(
        TimeSpan retryAfterValue,
        Uri? requestUri,
        SupportedProviders? provider,
        Exception innerException,
        string? endpoint = null
    ) : base( BuildMessage( retryAfterValue, requestUri, provider, endpoint ), innerException ) {
        RetryAfterValue = ValidateRetryAfter( retryAfterValue );
        RequestUri = requestUri;
        Provider = provider;
        Endpoint = endpoint;
    }

    private static TimeSpan ValidateRetryAfter( TimeSpan retryAfterValue ) {
        if (retryAfterValue < TimeSpan.Zero) {
            throw new ArgumentOutOfRangeException( nameof( retryAfterValue ), "Retry-After must be nonnegative." );
        }
        return retryAfterValue;
    }

    private static string BuildMessage( TimeSpan retryAfterValue, Uri? requestUri, SupportedProviders? provider, string? endpoint )
        => $"Rate limit encountered for {provider?.ToString( ) ?? "Unknown"}; retry after {retryAfterValue.TotalSeconds:F0} seconds. " +
           $"Endpoint: {endpoint ?? "Unknown"}. Request URI: {requestUri?.ToString( ) ?? "Unknown"}";
}
