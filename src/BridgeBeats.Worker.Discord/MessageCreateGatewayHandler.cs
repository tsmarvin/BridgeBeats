using System.Net;
using System.Text.RegularExpressions;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Worker.Discord.Extensions;
using BridgeBeats.Worker.Discord.Services;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;

namespace BridgeBeats.Worker.Discord {

    /// <summary>
    /// Handles inbound Discord gateway messages for this sharded worker. On each non-bot message it
    /// regex-extracts Spotify, Apple Music, and Tidal links, resolves them through
    /// <see cref="BridgeBeatsApiClient"/> (which calls the BridgeBeats Web API), posts the resulting
    /// cross-provider card(s) back to the channel, and deletes the original message when it contained
    /// only links. Deleting the original message is optional cleanup: a Discord 403 "Missing
    /// Permissions" response for the delete is treated as an expected outcome, since the reply has
    /// already been posted. The handler is shard-scoped: it ignores any message whose shard id does not
    /// match this node's <see cref="DiscordNodeConfig.NodeNumber"/>, so the Discord worker acts as a link
    /// producer for one partition of the gateway shard space.
    /// </summary>
    /// <param name="discordConfig">The node configuration that names the gateway shard this worker owns.</param>
    /// <param name="apiClient">The HTTP client used to call the BridgeBeats Web API for lookups and card storage.</param>
    public partial class MessageCreateGatewayHandler(
        DiscordNodeConfig discordConfig,
        BridgeBeatsApiClient apiClient
    ) : IMessageCreateShardedGatewayHandler {

        /// <summary>
        /// The Discord API error code reported when the bot lacks the permission required for the
        /// requested action. Returned in the response body's <c>code</c> field, not as an HTTP status.
        /// </summary>
        private const int MissingPermissionsErrorCode = 50013;

        /// <summary>The gateway shard id this handler accepts messages for; messages from other shards are ignored.</summary>
        private readonly int _nodeNumber = discordConfig.NodeNumber;

        /// <summary>The client used to resolve links and store cards via the BridgeBeats Web API.</summary>
        private readonly BridgeBeatsApiClient _apiClient = apiClient;

        /// <summary>
        /// Posts the resolved card for a single lookup result back to the originating channel. When
        /// the card service is enabled, it first stores the card via
        /// <see cref="BridgeBeatsApiClient.StoreCardAsync"/> and embeds the returned shareable URL;
        /// if storing fails (or the card service is disabled) it falls back to an embed without a card
        /// URL.
        /// </summary>
        /// <param name="client">The gateway client used to send the reply.</param>
        /// <param name="channelId">The channel to post the card into.</param>
        /// <param name="result">The cross-provider lookup result to render as an embed.</param>
        /// <param name="userId">The id of the user whose message triggered the lookup; mentioned in the reply.</param>
        /// <param name="apiClient">The Web API client used to store the shareable card when the card service is enabled.</param>
        /// <returns>A task that resolves to <see langword="true"/> once the message has been sent.</returns>
        internal static async Task<bool> SendLinkMessage(
            GatewayClient client,
            ulong channelId,
            MediaLinkResult result,
            ulong userId,
            BridgeBeatsApiClient apiClient
        ) {
            // When OpenGraph card service is enabled, use a Discord native embed with the card URL
            // as the clickable title/image link, while provider links remain inline and clickable
            if (apiClient.IsCardServiceEnabled) {
                string? cardUrl = await apiClient.StoreCardAsync( result );
                if (cardUrl != null) {
                    _ = await client.Rest.SendMessageAsync(
                        channelId,
                        result.ToDiscordMessagePropertiesWithCardUrl( userId, cardUrl )
                    );
                } else {
                    // Fall back to non-card embed if storage failed
                    _ = await client.Rest.SendMessageAsync(
                        channelId,
                        result.ToDiscordMessageProperties( userId )
                    );
                }
            } else {
                // When OpenGraph is disabled, use the original non-OpenGraph approach
                _ = await client.Rest.SendMessageAsync(
                    channelId,
                    result.ToDiscordMessageProperties( userId )
                );
            }
            return true;
        }

        /// <summary>
        /// Handles one inbound gateway message. Ignores messages from other shards (per
        /// <see cref="_nodeNumber"/>) and from bots, extracts any music links from the content, and
        /// for each one resolves it through the Web API and posts a card. If a card was sent and the
        /// message consisted only of those links, the original message is deleted to keep the channel
        /// clean.
        /// </summary>
        /// <param name="client">The gateway client that received the message.</param>
        /// <param name="message">The inbound Discord message.</param>
        /// <returns>A task that completes once the message has been processed.</returns>
        public async ValueTask HandleAsync( GatewayClient client, Message message ) {
            if (client.Shard.HasValue && client.Shard.Value.Id != _nodeNumber) { return; }
            if (message.Author.IsBot) { return; }

            string content = message.Content.Trim( );

            // Extract music links from the message content before API call
            List<string> inputLinks = ExtractMusicLinks( content );
            if (inputLinks.Count == 0) { return; }

            bool messageSent = false;
            await foreach (MediaLinkResult result in _apiClient.GetInfoAsync( content )) {
                if (result.Results.Count > 0) {
                    messageSent = await SendLinkMessage( client, message.ChannelId, result, message.Author.Id, _apiClient );
                }
            }

            // If we sent an embed message and the input message only contained valid links, delete the input message.
            // Deletion is optional cleanup: the reply has already been posted, so a missing-permission response for
            // the delete is expected rather than an error. Every other failure still propagates.
            if (messageSent && Regex.IsMatch( content, $"^{CombinedInputLinksRegexEscaped( inputLinks )}$" )) {
                await DeleteOriginalMessageAsync( async ( ) => await message.DeleteAsync( ) );
            }

            return;
        }

        /// <summary>
        /// Deletes the original message, absorbing only the Discord 403 "Missing Permissions" response that is
        /// returned when the bot was not granted Manage Messages in the channel. That case is an expected
        /// outcome because the card reply has already been posted; the channel simply keeps the original
        /// message. Any other failure propagates unchanged.
        /// </summary>
        /// <param name="deleteAsync">The deletion call to run.</param>
        /// <returns>A task that completes once the deletion has been attempted.</returns>
        internal static async Task DeleteOriginalMessageAsync( Func<Task> deleteAsync ) {
            try {
                await deleteAsync( );
            } catch (RestException ex) when (IsMissingPermissions( ex )) {
                // Expected: optional cleanup the bot is not permitted to perform.
            }
        }

        /// <summary>
        /// Determines whether a REST failure is specifically a Discord 403 "Missing Permissions" response,
        /// which the API reports with HTTP status <see cref="HttpStatusCode.Forbidden"/> and body code
        /// <see cref="MissingPermissionsErrorCode"/>. Requiring both keeps an unrelated 403 (for example an
        /// invalid or unauthorized token, or a different permission error such as Missing Access) from being
        /// silently absorbed as an expected outcome.
        /// </summary>
        /// <param name="exception">The REST exception raised by a Discord API call.</param>
        /// <returns><see langword="true"/> only for the missing-permissions response.</returns>
        internal static bool IsMissingPermissions( RestException exception )
            => exception.StatusCode == HttpStatusCode.Forbidden
                && exception.Error?.Code == MissingPermissionsErrorCode;

        /// <summary>
        /// Extracts every Spotify, Apple Music, or Tidal link found in the message content, in the
        /// order they appear, using <see cref="MusicLinkPattern"/>.
        /// </summary>
        /// <param name="content">The trimmed message text to scan.</param>
        /// <returns>The matched music links; empty when the content contains none.</returns>
        private static List<string> ExtractMusicLinks( string content ) {
            List<string> links = [];

            // Match Spotify, Apple Music, and Tidal URLs
            MatchCollection matches = MusicLinkPattern( ).Matches( content );
            foreach (Match match in matches) {
                links.Add( match.Value );
            }

            return links;
        }

        /// <summary>
        /// The compiled pattern that matches a Spotify, Apple Music, or Tidal link. Matching is
        /// case-insensitive and recognizes the <c>open.spotify.com</c>, <c>music.apple.com</c>,
        /// <c>tidal.com</c>, and <c>listen.tidal.com</c> hosts.
        /// </summary>
        /// <returns>The source-generated <see cref="Regex"/> for music links.</returns>
        [GeneratedRegex( @"https?://(?:open\.spotify\.com|music\.apple\.com|tidal\.com|listen\.tidal\.com)/\S+", RegexOptions.IgnoreCase )]
        private static partial Regex MusicLinkPattern( );

        /// <summary>
        /// Builds a regex fragment that matches the supplied links in sequence separated only by
        /// whitespace. Used to test whether a message consisted of nothing but the extracted links
        /// (in which case the original is deleted after the card is posted).
        /// </summary>
        /// <param name="inputLinks">The links to join, each escaped for literal matching.</param>
        /// <returns>A whitespace-separated, regex-escaped concatenation of the links.</returns>
        private static string CombinedInputLinksRegexEscaped( List<string> inputLinks )
            => string.Join( @"\s*", inputLinks.Select( Regex.Escape ) );

    }
}
