using System.Text.Json.Serialization;

namespace BridgeBeats.Core.Domain.Providers.Tidal.Models {

    /// <summary>
    /// DTO mirroring the <c>attributes</c> object of a Tidal API resource.
    /// </summary>
    /// <remarks>
    /// A single attributes shape covers every resource type this lookup reads
    /// (tracks, albums, artists, artworks, genres), so only the member relevant to the
    /// resource's <see cref="TidalResource.Type"/> is populated for a given resource.
    /// For example a track populates <see cref="Title"/> and <see cref="Isrc"/>, an
    /// album populates <see cref="Title"/> and <see cref="BarcodeId"/>, an artist
    /// populates <see cref="Name"/>, and an artwork populates <see cref="MediaType"/>
    /// and <see cref="Files"/>. The shape mirrors Tidal's wire format and is a
    /// deserialization target only.
    /// </remarks>
    public sealed class TidalAttributes {

        /// <summary>
        /// Gets or sets the track or album title, mapped from the Tidal <c>title</c>
        /// member.
        /// </summary>
        [JsonPropertyName( "title" )]
        public string? Title { get; set; }

        /// <summary>
        /// Gets or sets the artist name, mapped from the Tidal <c>name</c> member.
        /// Genre names are carried by the separate <see cref="GenreName"/> field,
        /// not by this property.
        /// </summary>
        [JsonPropertyName( "name" )]
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets the genre name, mapped from the Tidal <c>genreName</c> member.
        /// This is populated only for resources whose type is <c>genres</c>.
        /// </summary>
        [JsonPropertyName( "genreName" )]
        public string? GenreName { get; set; }

        /// <summary>
        /// Gets or sets the track's ISRC, mapped from the Tidal <c>isrc</c> member.
        /// </summary>
        [JsonPropertyName( "isrc" )]
        public string? Isrc { get; set; }

        /// <summary>
        /// Gets or sets the album's barcode (UPC), mapped from the Tidal
        /// <c>barcodeId</c> member.
        /// </summary>
        [JsonPropertyName( "barcodeId" )]
        public string? BarcodeId { get; set; }

        /// <summary>
        /// Gets or sets the resource's external links, mapped from the Tidal
        /// <c>externalLinks</c> member. The first entry is used as the public Tidal
        /// URL for the track or album.
        /// </summary>
        [JsonPropertyName( "externalLinks" )]
        public List<TidalExternalLink>? ExternalLinks { get; set; }

        /// <summary>
        /// Gets or sets the media type of an artwork resource, mapped from the Tidal
        /// <c>mediaType</c> member (for example <c>"IMAGE"</c>).
        /// </summary>
        [JsonPropertyName( "mediaType" )]
        public string? MediaType { get; set; }

        /// <summary>
        /// Gets or sets the artwork files, mapped from the Tidal <c>files</c> member.
        /// The first entry's href is used as the album art URL.
        /// </summary>
        [JsonPropertyName( "files" )]
        public List<TidalFile>? Files { get; set; }
    }

}
