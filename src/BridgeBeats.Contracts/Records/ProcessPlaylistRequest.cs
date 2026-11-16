using System.ComponentModel.DataAnnotations;

namespace BridgeBeats.Contracts.Records {

    /// <summary>Request to process an Apple Music playlist, identified by its id.</summary>
    /// <param name="PlaylistId">
    /// The Apple Music library playlist id to process. Must match the Apple library-playlist identifier
    /// grammar: alphanumeric segments separated by single dots, underscores, or hyphens. The value
    /// must begin and end with an alphanumeric character; leading, trailing, or consecutive
    /// separator characters are not permitted. Accepted forms include <c>p.&lt;alphanumeric&gt;</c>
    /// (e.g. <c>p.ABCdef1234</c>), <c>i.&lt;alphanumeric&gt;</c> (e.g. <c>i.e5gmPS6rZ856</c>), and
    /// <c>pl.u-&lt;alphanumeric&gt;</c> (e.g. <c>pl.u-abc123</c>). Maximum 100 characters.
    /// </param>
    public record ProcessPlaylistRequest(
        [Required]
        [StringLength( 100, MinimumLength = 1 )]
        [RegularExpression(
            @"\A[A-Za-z0-9]+([._-][A-Za-z0-9]+)*\z",
            ErrorMessage = "PlaylistId contains invalid characters. Only alphanumeric characters are allowed, with dots, underscores, or hyphens permitted as single separators between alphanumeric segments. Leading, trailing, and consecutive separator characters are not allowed."
        )]
        string PlaylistId
    );

}
