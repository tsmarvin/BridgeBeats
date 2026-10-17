using System.Text.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Services;
using BridgeBeats.Core.Infrastructure.Logging;

namespace BridgeBeats.Core.Domain.Services.Queue;

/// <summary>
/// Merges the per-provider results of a distributed lookup saga into a single
/// <see cref="MediaLinkResult"/>. Each provider's stored <see cref="ProviderLookupState.ResultJson"/>
/// is deserialized and the successful results are combined, with the first marked as primary.
/// </summary>
/// <remarks>
/// This combiner is consumed by the out-of-process saga coordinator that finalizes completed sagas;
/// it is not part of the in-process resolution path and is not registered in this layer's
/// dependency injection. It is documented here because the type lives in this assembly.
/// </remarks>
public sealed partial class SagaResultCombiner {
    /// <summary>The logger for combination diagnostics (incomplete sagas, deserialize failures).</summary>
    private readonly ILogger<SagaResultCombiner> _logger;
    /// <summary>JSON options (camelCase) used to deserialize each provider's stored result.</summary>
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new combiner with the given logger and a camelCase JSON deserialization policy.
    /// </summary>
    /// <param name="logger">The logger for combination diagnostics.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="logger"/> is null.</exception>
    public SagaResultCombiner( ILogger<SagaResultCombiner> logger ) {
        _logger = logger ?? throw new ArgumentNullException( nameof( logger ) );
        _jsonOptions = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    /// <summary>
    /// Combines the successful per-provider results recorded in a saga into a single
    /// <see cref="MediaLinkResult"/>. Providers without a successful result (or with unparseable
    /// JSON) are skipped. Returns <see langword="null"/> when no provider produced a usable result.
    /// </summary>
    /// <param name="saga">The saga whose per-provider results are merged.</param>
    /// <param name="allowIncomplete">
    /// When <see langword="false"/> and the saga is not yet complete, a warning is logged before
    /// combining the partial results; combining still proceeds. When <see langword="true"/>, no
    /// warning is logged.
    /// </param>
    /// <returns>The combined result, or <see langword="null"/> when there are no successful provider results.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="saga"/> is null.</exception>
    public MediaLinkResult? CombineResults( LookupSagaState saga, bool allowIncomplete = false ) {
        ArgumentNullException.ThrowIfNull( saga );

        if (!saga.IsComplete && !allowIncomplete) {
            LogCombineResultsIncompleteSaga( _logger, saga.SagaId );
        }

        // Extract successful results from the saga
        List<(SupportedProviders provider, MusicLookupResult result)> successfulResults = [];

        foreach ((SupportedProviders provider, ProviderLookupState state) in saga.ProviderStates) {
            if (!state.IsSuccess || string.IsNullOrEmpty( state.ResultJson )) {
                continue;
            }

            try {
                MusicLookupResult? result = JsonSerializer.Deserialize<MusicLookupResult>(
                    state.ResultJson,
                    _jsonOptions
                );

                if (result is not null) {
                    if (MediaLookupResultIdentity.IsPersistable( result )) {
                        successfulResults.Add( (provider, result) );
                    } else {
                        LogNonPersistableResultSkipped( _logger, provider, saga.SagaId );
                    }
                }
            } catch (JsonException ex) {
                LogDeserializeResultFailed( _logger, ex, provider, saga.SagaId );
            }
        }

        if (successfulResults.Count == 0) {
            LogNoSuccessfulResults( _logger, saga.SagaId );
            return null;
        }

        return BuildMediaLinkResult( successfulResults, saga );
    }

    /// <summary>
    /// Combines an ad-hoc set of already-deserialized per-provider results into a single
    /// <see cref="MediaLinkResult"/>, marking the first as primary. Returns <see langword="null"/>
    /// when the set is empty.
    /// </summary>
    /// <param name="results">The per-provider results to combine.</param>
    /// <param name="lookupValue">The lookup value these results came from, used only for log context.</param>
    /// <returns>The combined result, or <see langword="null"/> when <paramref name="results"/> is empty.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="results"/> is null.</exception>
    public MediaLinkResult? CombineResults(
        IEnumerable<(SupportedProviders provider, MusicLookupResult result)> results,
        string lookupValue
    ) {
        ArgumentNullException.ThrowIfNull( results );

        List<(SupportedProviders provider, MusicLookupResult result)> resultList = [.. results];

        if (resultList.Count == 0) {
            return null;
        }

        // Create a minimal saga state for the build method
        LookupSagaState saga = new( ) {
            SagaId = string.Empty,
            LookupKey = lookupValue,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = lookupValue
        };

        return BuildMediaLinkResult( resultList, saga );
    }

    /// <summary>
    /// Assembles the final <see cref="MediaLinkResult"/> from the ordered provider results, marking
    /// the first entry as primary and indexing each by its provider.
    /// </summary>
    /// <param name="results">The ordered per-provider results; the first is treated as primary.</param>
    /// <param name="saga">The saga the results came from, used only for log context.</param>
    /// <returns>A combined result keyed by provider.</returns>
    private MediaLinkResult BuildMediaLinkResult(
        List<(SupportedProviders provider, MusicLookupResult result)> results,
        LookupSagaState saga
    ) {
        MediaLinkResult mediaLinkResult = new( );

        // Mark the first result as primary
        bool isFirst = true;
        foreach ((SupportedProviders provider, MusicLookupResult result) in results) {
            result.IsPrimary = isFirst;
            isFirst = false;

            mediaLinkResult.Results[provider] = result;
        }

        LogCombinedResults( _logger, results.Count, saga.SagaId );

        return mediaLinkResult;
    }

    #region LoggerMessage Definitions

    /// <summary>Logs (Warning) that results were combined for a saga that is not yet complete.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.CombineResultsIncompleteSaga,
        Level = LogLevel.Warning,
        Message = "Attempted to combine results for incomplete saga {SagaId}" )]
    private static partial void LogCombineResultsIncompleteSaga( ILogger logger, string sagaId );

    /// <summary>Logs (Warning) that a provider's stored result JSON could not be deserialized.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The deserialization exception.</param>
    /// <param name="provider">The provider whose result failed to parse.</param>
    /// <param name="sagaId">The saga identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.DeserializeResultFailed,
        Level = LogLevel.Warning,
        Message = "Failed to deserialize result for provider {Provider} in saga {SagaId}" )]
    private static partial void LogDeserializeResultFailed( ILogger logger, Exception ex, SupportedProviders provider, string sagaId );

    /// <summary>Logs (Warning) that a saga has no successful provider results to combine.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.NoSuccessfulResults,
        Level = LogLevel.Warning,
        Message = "Saga {SagaId} has no successful results yet" )]
    private static partial void LogNoSuccessfulResults( ILogger logger, string sagaId );

    /// <summary>Logs (Debug) how many provider results were combined for a saga.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="count">The number of provider results combined.</param>
    /// <param name="sagaId">The saga identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.CombinedResults,
        Level = LogLevel.Debug,
        Message = "Combined {Count} provider results for saga {SagaId}" )]
    private static partial void LogCombinedResults( ILogger logger, int count, string sagaId );

    /// <summary>Logs that a nominally successful provider result had no durable identity.</summary>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.NonPersistableResultSkipped,
        Level = LogLevel.Warning,
        Message = "Skipping non-persistable successful result for provider {Provider} in saga {SagaId}" )]
    private static partial void LogNonPersistableResultSkipped(
        ILogger logger,
        SupportedProviders provider,
        string sagaId );

    #endregion LoggerMessage Definitions
}
