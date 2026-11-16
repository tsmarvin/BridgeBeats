# Discord Bot Guide

This guide explains how to use the BridgeBeats Discord bot, including setup and what to expect when sharing music links in your Discord server.

## Architecture

The Discord integration runs as a standalone worker service (`BridgeBeats.Worker.Discord`) that:

- Connects to Discord via the NetCord gateway library
- Monitors server channels for music links
- Calls the BridgeBeats Web API for music lookups
- Posts conversion embeds back to Discord channels

This separation lets the Discord bot scale independently of the web application and enables horizontal scaling through gateway sharding: each worker handles one gateway shard, selected by the `BridgeBeats:NodeNumber` setting, and ignores messages from other shards.

## Getting Started

### Adding the bot to your server

1. Click to [add the Discord bot](https://discord.com/oauth2/authorize?client_id=1417324891161759815)
2. Select the Discord server where you want to add the bot
3. Click **Authorize**

The bot will now appear in your server and is ready to convert music links.

See [Discord Bot Server Settings](DISCORD_SETTINGS.md) to configure which channels the bot can use
and whether it removes link-only messages.

## How It Works

### Sharing music links

When you or anyone in your server shares a music link from **Spotify**, **Apple Music**, or **Tidal**, BridgeBeats automatically:

1. Detects the link
2. Finds the same track or album on other music platforms
3. Posts an embed with:
   - Album artwork
   - Links to all available platforms
   - Credit to whoever shared the link

The bot recognizes links on the `open.spotify.com`, `music.apple.com`, `tidal.com`, and `listen.tidal.com` hosts.

For a link-only message, BridgeBeats posts the conversion and removes the original message:

![BridgeBeats response for All I Want by Bob Moses](images/Discord/SharedTrack-1.png)

![BridgeBeats response for Send Me On My Way by Rusted Root](images/Discord/SharedTrack-2.png)

### Message cleanup

To keep your channels clean, BridgeBeats automatically **deletes link-only messages** after posting the conversion embed. For example:

- **Deleted**: A message containing just `https://open.spotify.com/track/...`
- **Deleted**: Multiple music links with only whitespace between them
- **Kept**: A message like "Check this out! https://open.spotify.com/track/..."
- **Kept**: Any message with text or other content alongside the link

The key rule: if your message is only music links (and whitespace), it gets deleted. Otherwise, it stays.

If you want to keep a link around, just add a comment or emoji with it.

In this example, the user's comment remains visible while BridgeBeats posts the converted links
below it:

![A music link shared with extra text remains above the BridgeBeats response](images/Discord/SharedTrack-Extra-Text.png)

## Channel Permissions

### Required bot permissions

For the bot to work properly, it needs these permissions in your channels:

| Permission | Purpose |
|-----------|---------|
| **View Channel** | Detect supported music links in the channel |
| **Send Messages** | Post conversion embeds |
| **Embed Links** | Display music previews with rich formatting |
| **Manage Messages** | Delete link-only messages to prevent multiple embeds for the same content |

**Note:** If the bot lacks the **Manage Messages** permission, it will still convert links and post embeds, but won't be able to delete the original link-only messages.

For illustrated setup instructions, see [Discord Bot Server Settings](DISCORD_SETTINGS.md).

### Restricting the bot to specific channels

If you want to limit where BridgeBeats operates in your server:

1. Go to **Server Settings** → **Roles**
2. Find the **BridgeBeats** role
3. Go to the channel you want to restrict
4. Edit **Channel Permissions**
5. Find the **BridgeBeats** role and deny **Send Messages** or **View Channel**

## Troubleshooting

### Bot not responding

1. **Check if the bot is online**: Look at the server member list. You should see the BridgeBeats bot with a green online indicator.
2. **Verify permissions**: Make sure the bot has **View Channel**, **Send Messages**, and **Embed Links** permissions.
3. **Validate the link**: Ensure you're sharing a valid link from Spotify, Apple Music, or Tidal (not a screenshot or description).
4. **Try directly on the BridgeBeats website**: Validate the link on [BridgeBeats](https://bridgebeats.link) to see if it can find matches.

### Bot can't find a match

Sometimes a track or album isn't available on all platforms. If the bot doesn't respond to your link, it means:

- The song or album isn't available on the other platforms
- It may have been removed or is region-restricted
- The bot will simply skip it (no error message or response)

Try a different song or check whether it's available in your region on the music app directly.

### Messages not deleting

The bot only deletes **link-only messages**. If your message has any text or other content with the link, it is preserved so your conversation isn't lost.

The bot also needs the **Manage Messages** permission to delete messages. Without that permission, links won't be deleted (but they'll still be converted).

## FAQ

**Q: What platforms does the bot support?**
A: Spotify, Apple Music, and Tidal. Share a link from any of these, and get matches on the others.

**Q: Does the bot work in DMs?**
A: No, it only works in server channels.

**Q: Can I prevent the bot from deleting my message?**
A: Yes. Add a comment, reaction, or any text with your link and it won't be deleted.

**Q: What if the bot is broken or not responding?**
A: First, check that the bot is online in your server member list and has the required permissions.
If it is still not working, ask for help in the
[BridgeBeats support server](https://discord.gg/T98sGP2nX8) or submit an issue on
[GitHub](https://github.com/tsmarvin/BridgeBeats/issues).

**Q: Who can see the bot's messages?**
A: Everyone in the channel can see the conversion embeds posted by the bot. The embeds display music service URLs (Spotify, Apple Music, Tidal links) and album artwork so anyone in the channel can access the music from their preferred platform. Your original message is visible to everyone until deleted (which only happens for link-only messages).

**Q: What information does the bot store?**
A: The bot sees the content of all messages in channels where it has access, including user IDs and message content. It does not store or log this information beyond what is necessary for link conversion. The only information stored or logged are the parsed URLs shared for conversion and the Discord user ID of the person who shared them. The Discord user ID is only logged if an error occurs during processing, to help with debugging.

---

For help, join the [BridgeBeats support server](https://discord.gg/T98sGP2nX8). To report an issue,
visit the [BridgeBeats GitHub](https://github.com/tsmarvin/BridgeBeats).
