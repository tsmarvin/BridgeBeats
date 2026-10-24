using BridgeBeats.Contracts.Records;

namespace BridgeBeats.Contracts.Interfaces;

/// <summary>
/// Provides typed, concurrency-safe access to the encrypted application-settings aggregate.
/// </summary>
public interface IApplicationSettingsService {
    /// <summary>Loads a redacted settings representation suitable for ordinary display.</summary>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The redacted status, or <see langword="null"/> before initial configuration.</returns>
    Task<ApplicationSettingsStatus?> GetStatusAsync( CancellationToken cancellationToken = default );

    /// <summary>Validates and atomically saves an application-settings update.</summary>
    /// <param name="update">The complete non-secret values, secret changes, and expected revision.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The redacted status after the successful update.</returns>
    Task<ApplicationSettingsStatus> UpdateAsync(
        ApplicationSettingsUpdate update,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Provides decrypted settings only to trusted runtime composition code. Display and administration
/// consumers should depend on <see cref="IApplicationSettingsService"/>, which cannot reveal secrets.
/// </summary>
public interface IApplicationSettingsRuntimeReader {
    /// <summary>Loads settings for an authorized runtime consumer.</summary>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The settings snapshot, or <see langword="null"/> before initial configuration.</returns>
    Task<ApplicationSettingsSnapshot?> GetRuntimeSettingsAsync( CancellationToken cancellationToken = default );
}
