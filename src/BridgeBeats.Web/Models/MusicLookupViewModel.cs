using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;

namespace BridgeBeats.Web.Models {
    /// <summary>
    /// View model for a music lookup result page, holding the resolved result items and an optional message.
    /// </summary>
    public class MusicLookupViewModel {
        /// <summary>
        /// The resolved lookup result items.
        /// </summary>
        public List<MusicLookupResultItem> Items { get; set; } = [];

        /// <summary>
        /// An optional message describing the outcome (for example, when no results were found).
        /// </summary>
        public string? Message { get; set; }

        /// <summary>
        /// A single resolved lookup result, pairing the cross-provider media link with its shareable card,
        /// ATProto record URI, and the provider it was primarily resolved from.
        /// </summary>
        public class MusicLookupResultItem {
            /// <summary>
            /// The URL of the shareable Open Graph card for this result, when one has been generated.
            /// </summary>
            public string? CardUrl { get; set; }

            /// <summary>
            /// The ATProto record URI where this result is stored, when available.
            /// </summary>
            public string? ATProtoUri { get; set; }

            /// <summary>
            /// The cross-provider media link result.
            /// </summary>
            public MediaLinkResult Result { get; set; } = null!;

            /// <summary>
            /// The provider this result was primarily resolved from.
            /// </summary>
            public SupportedProviders PrimaryProvider { get; set; }

            /// <summary>
            /// The per-provider lookup result for <see cref="PrimaryProvider"/>.
            /// </summary>
            public MusicLookupResult PrimaryResult { get; set; } = null!;

            /// <summary>
            /// <see langword="true"/> when a non-finalized saga is still active for this result,
            /// meaning the lookup is in progress and more provider results may arrive.
            /// </summary>
            public bool IsLookupInProgress { get; set; }
        }
    }
}
