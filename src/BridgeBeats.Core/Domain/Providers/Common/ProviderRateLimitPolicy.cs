using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.Enums;

namespace BridgeBeats.Core.Domain.Providers.Common;

/// <summary>
/// Owns provider-specific rate-limit granularity and coverage. Transported endpoint shapes remain
/// diagnostic; tracking keys are the bounded values persisted in Redis and used for admission.
/// </summary>
internal static class ProviderRateLimitPolicy {
    internal static string ToTrackingKey( SupportedProviders provider, string endpoint ) {
        string normalized = ProviderEndpointConstants.Normalize( endpoint );

        return provider == SupportedProviders.Spotify
            && !normalized.Equals( ProviderEndpointConstants.AuthToken, StringComparison.Ordinal )
                ? ProviderEndpointConstants.ProviderWide
                : normalized;
    }

    internal static bool Covers(
        SupportedProviders provider,
        string storedEndpoint,
        string requestedEndpoint
    ) {
        string storedKey = ToTrackingKey( provider, storedEndpoint );
        string requestedKey = ToTrackingKey( provider, requestedEndpoint );
        return storedKey.Equals( requestedKey, StringComparison.Ordinal )
            || storedKey.Equals( ProviderEndpointConstants.ProviderWide, StringComparison.Ordinal )
                && !requestedKey.Equals( ProviderEndpointConstants.AuthToken, StringComparison.Ordinal );
    }

    internal static string? GetAdmissionKey(
        SupportedProviders provider,
        string? deferredEndpoint
    ) {
        if (!string.IsNullOrWhiteSpace( deferredEndpoint )) {
            return ToTrackingKey( provider, deferredEndpoint );
        }

        return provider == SupportedProviders.Spotify
            ? ProviderEndpointConstants.ProviderWide
            : null;
    }

    internal static bool IsBlocked(
        SupportedProviders provider,
        IReadOnlySet<string> blockedEndpoints,
        string? deferredEndpoint
    ) {
        string? requestedKey = GetAdmissionKey( provider, deferredEndpoint );
        if (blockedEndpoints.Contains( ProviderEndpointConstants.ProviderWide )) {
            return requestedKey is null
                || !requestedKey.Equals( ProviderEndpointConstants.AuthToken, StringComparison.Ordinal );
        }

        return requestedKey is not null
            && blockedEndpoints.Contains( requestedKey );
    }
}
