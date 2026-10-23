using System.Text.Json.Nodes;

namespace BridgeBeats.Core.Infrastructure.Storage.Car;

/// <summary>
/// Facade that orchestrates the full CAR read pipeline for a single collection: decode the CARv1
/// buffer, walk the MST, filter to the target collection, and convert each value block to JSON.
/// The only type <see cref="ATProtoStorageService"/> touches directly.
/// </summary>
/// <remarks>
/// This is the top-level entry point that ties together <see cref="CarV1Reader"/>,
/// <see cref="MstWalker"/>, and <see cref="DagCborConverter"/>. MST keys take the form
/// <c>{collection}/{rkey}</c>; only keys whose collection segment matches the requested NSID are
/// returned, with the collection prefix stripped to leave the bare record key.
/// </remarks>
internal static class CarRepoReader {

    /// <summary>
    /// Decodes a repository CAR and returns the records of a single collection as <c>(rkey, JSON)</c> pairs.
    /// </summary>
    /// <param name="car">The complete CARv1 byte content of the repository, from com.atproto.sync.getRepo.</param>
    /// <param name="collectionNsid">The collection NSID to filter to (for example <c>link.bridgebeats.lookup</c>).</param>
    /// <param name="cancellationToken">A token forwarded to the MST walk to cancel a long-running walk.</param>
    /// <returns>
    /// A <see cref="CarEnumerationResult"/> carrying the lazy record sequence, the total block count,
    /// and the commit version. The caller is responsible for acting on a non-3 commit version.
    /// </returns>
    /// <exception cref="CarParseException">
    /// Thrown (during decoding or during lazy iteration of the records) when the CAR is malformed, a
    /// safety bound is breached, a referenced value block is missing, or a record decodes to null.
    /// Callers outside Core can only catch broadly, since <see cref="CarParseException"/> is internal.
    /// </exception>
    internal static CarEnumerationResult EnumerateCollection(
        ReadOnlyMemory<byte> car,
        string collectionNsid,
        CancellationToken cancellationToken = default
    ) {
        string prefix = collectionNsid + "/";
        CarFile carFile = CarV1Reader.Read( car );
        int blockCount = carFile.Blocks.Count;

        (IEnumerable<(string Key, string ValueCidHex)> records, int commitVersion) =
            MstWalker.EnumerateRecords( carFile, cancellationToken );

        return new CarEnumerationResult(
            Records: ProjectRecords( records, carFile, prefix ),
            BlockCount: blockCount,
            CommitVersion: commitVersion
        );
    }

    /// <summary>
    /// Filters the MST key stream to the target collection, strips the collection prefix to recover
    /// each record key, and decodes each matching value block to JSON.
    /// </summary>
    /// <param name="records">The full MST key stream as <c>(Key, ValueCidHex)</c> pairs.</param>
    /// <param name="carFile">The decoded CAR file used to resolve value blocks by CID.</param>
    /// <param name="prefix">The collection prefix (<c>{collection}/</c>) to match and strip.</param>
    /// <returns>The lazy sequence of <c>(rkey, record JSON)</c> pairs for the collection.</returns>
    /// <exception cref="CarParseException">Thrown when a value block referenced by a matching key is missing or decodes to null.</exception>
    private static IEnumerable<(string Rkey, string Cid, JsonNode Record)> ProjectRecords(
        IEnumerable<(string Key, string ValueCidHex)> records,
        CarFile carFile,
        string prefix
    ) {
        foreach ((string key, string valueCidHex) in records) {
            if (!key.StartsWith( prefix, StringComparison.Ordinal )) {
                continue;
            }

            string rkey = key[prefix.Length..];

            if (!carFile.Blocks.TryGetValue( valueCidHex, out ReadOnlyMemory<byte> blockBytes )) {
                throw new CarParseException( $"Value block {valueCidHex} for key '{key}' not found in CAR." );
            }

            JsonNode recordNode = DagCborConverter.ToJsonNode( blockBytes )
                ?? throw new CarParseException( $"Record block for key '{key}' deserialized to null." );

            yield return (rkey, Cid.FromKeyHex( valueCidHex ).ToString( ), recordNode);
        }
    }
}

/// <summary>
/// The result of enumerating one collection out of a repository CAR.
/// </summary>
/// <param name="Records">
/// The lazy sequence of <c>(rkey, record JSON)</c> pairs for the collection. Iterating it drives the
/// MST walk and per-record decoding, so parse failures can surface during enumeration.
/// </param>
/// <param name="BlockCount">The total number of blocks in the decoded CAR (used for logging and as the walk's count caps).</param>
/// <param name="CommitVersion">The repository commit version read from the commit block.</param>
internal sealed record CarEnumerationResult(
    IEnumerable<(string Rkey, string Cid, System.Text.Json.Nodes.JsonNode Record)> Records,
    int BlockCount,
    int CommitVersion
);
