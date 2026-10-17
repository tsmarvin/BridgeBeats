namespace BridgeBeats.Web.Streaming;

/// <summary>
/// Defines the browser-facing framing protocol used by streamed HTML responses.
/// </summary>
public static class StreamFraming {

    /// <summary>Delimiter separating complete HTML items in a streaming response.</summary>
    public const string ItemDelimiter = "<!--bridgebeats-stream-item-->";
}
