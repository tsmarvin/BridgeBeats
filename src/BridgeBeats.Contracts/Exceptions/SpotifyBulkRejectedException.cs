using BridgeBeats.Contracts.Enums;

namespace BridgeBeats.Contracts.Exceptions {

    /// <summary>
    /// Thrown when the Spotify bulk-lookup endpoint returns HTTP 400 Bad Request. Carries the HTTP
    /// status code and request context so the processor can route the batch to the individual
    /// single-id path rather than retrying as a batch.
    /// </summary>
    /// <remarks>
    /// A 400 Bad Request from the bulk endpoint is data-dependent: one id in the batch is malformed
    /// or otherwise unacceptable to the API. Re-enqueueing each id individually at Interactive
    /// priority isolates the poison id so the remaining ids resolve normally via the per-message
    /// queue processor. Other non-2xx responses (401, 403, 404, 5xx) are not thrown as this
    /// exception; those fall through to the batch back-off path in the caller.
    /// </remarks>
    public class SpotifyBulkRejectedException : Exception {

        /// <summary>
        /// The HTTP status code returned by the Spotify bulk endpoint.
        /// </summary>
        public int HttpStatusCode { get; }

        /// <summary>
        /// The request URI that was rejected, if known; otherwise <see langword="null"/>.
        /// </summary>
        public Uri? RequestUri { get; }

        /// <summary>
        /// The provider that issued the rejection (<see cref="SupportedProviders.Spotify"/>).
        /// </summary>
        public SupportedProviders Provider { get; }

        /// <summary>
        /// Initializes a new instance with the rejection context, building a descriptive message.
        /// </summary>
        /// <param name="httpStatusCode">The HTTP status code returned by the bulk endpoint.</param>
        /// <param name="requestUri">The rejected request URI, or <see langword="null"/> if unknown.</param>
        /// <param name="provider">The provider that issued the rejection.</param>
        public SpotifyBulkRejectedException(
            int httpStatusCode,
            Uri? requestUri,
            SupportedProviders provider
        ) : base( BuildMessage( httpStatusCode, requestUri, provider ) ) {
            HttpStatusCode = httpStatusCode;
            RequestUri = requestUri;
            Provider = provider;
        }

        /// <summary>
        /// Initializes a new instance with the rejection context and an inner exception.
        /// </summary>
        /// <param name="httpStatusCode">The HTTP status code returned by the bulk endpoint.</param>
        /// <param name="requestUri">The rejected request URI, or <see langword="null"/> if unknown.</param>
        /// <param name="provider">The provider that issued the rejection.</param>
        /// <param name="innerException">The exception that is the cause of this exception.</param>
        public SpotifyBulkRejectedException(
            int httpStatusCode,
            Uri? requestUri,
            SupportedProviders provider,
            Exception innerException
        ) : base( BuildMessage( httpStatusCode, requestUri, provider ), innerException ) {
            HttpStatusCode = httpStatusCode;
            RequestUri = requestUri;
            Provider = provider;
        }

        private static string BuildMessage(
            int httpStatusCode,
            Uri? requestUri,
            SupportedProviders provider
        ) {
            string providerName = provider.ToString( );
            string uri = requestUri?.ToString( ) ?? "Unknown";
            return $"Spotify bulk request rejected by {providerName} with HTTP {httpStatusCode}. " +
                   $"Request URI: {uri}. Batch will be re-enqueued as individual lookups.";
        }
    }
}
