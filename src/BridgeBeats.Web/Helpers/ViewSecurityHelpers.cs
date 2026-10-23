using System.Text.Encodings.Web;
using System.Text.Json;

namespace BridgeBeats.Web;

/// <summary>
/// View-layer security helpers shared across Razor views.
/// </summary>
internal static class ViewSecurityHelpers {
    /// <summary>
    /// <see cref="JsonSerializerOptions"/> whose encoder is the strict HTML-safe default
    /// (<see cref="JavaScriptEncoder.Default"/>). Use this instance wherever
    /// <c>Html.Raw(JsonSerializer.Serialize(...))</c> renders into an HTML attribute or
    /// script context so the safe encoding is explicit rather than incidental.
    /// </summary>
    /// <remarks>
    /// The encoder must remain <see cref="JavaScriptEncoder.Default"/> and must not be replaced
    /// with <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>. The <c>data-title</c> sinks
    /// in the Razor views use single-quote attribute delimiters (<c>data-title='...'</c>), and
    /// <see cref="JavaScriptEncoder.Default"/> escapes U+0027 (apostrophe / single quote) as
    /// <c>'</c>. Switching to the relaxed encoder would leave single quotes unescaped,
    /// allowing a title containing an apostrophe to close the attribute early and inject content.
    /// </remarks>
    internal static readonly JsonSerializerOptions HtmlSafeJsonOptions =
        new( ) { Encoder = JavaScriptEncoder.Default };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="url"/> is an absolute URI whose
    /// scheme is exactly <c>http</c> or <c>https</c>.  All other values — including
    /// <see langword="null"/>, empty strings, relative paths, and <c>javascript:</c> /
    /// <c>data:</c> URIs — return <see langword="false"/>.
    /// </summary>
    /// <param name="url">The URL to test.</param>
    internal static bool IsHttpOrHttps( string? url ) {
        if (string.IsNullOrWhiteSpace( url )) {
            return false;
        }

        return Uri.TryCreate( url, UriKind.Absolute, out Uri? parsed )
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
    }
}
