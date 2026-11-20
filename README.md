# BotReplay

A [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) plugin for CS2 that records your movement during [MatchZy](https://github.com/shobhit-pathak/MatchZy) practice and replays it on an enemy bot.

## Usage

Join T or CT and enter MatchZy practice with `.prac`. These commands require the `@css/root` permission:

- `.botrec` starts recording your movement. You must be alive; an enemy bot is not needed until playback.
- `.botstop` finishes the recording.
- `.botplay` replays it on a living enemy bot, starting at your recorded position. Add one to the opposing team before playing.
- `.botcancel` stops recording or playback.

Recordings are held in memory for the current map, up to two minutes. They are lost when the map changes, the plugin reloads or the server restarts. A new recording replaces the previous one. Recording ends if the player dies or disconnects. Fights and other interactions may change the outcome of playback. Player movement and input are recorded; grenade and weapon interactions have not been fully verified.

The plugin reads the loaded MatchZy instance's practice flag. It refuses commands and stops active recording or replay when practice ends. If the MatchZy instance or its practice flag cannot be found, it fails closed. This is based on MatchZy 0.9.x's `isPractice` field and may need updating if MatchZy changes its internals.

## Install

Requires a Linux CS2 dedicated server with Metamod:Source, CounterStrikeSharp 1.0.379 or newer (.NET 10), and MatchZy. Download the latest `BotReplay-v*.zip` from [Releases](https://github.com/axel0806/botreplay/releases), extract it into the server's `game/csgo/` directory, and restart the server. The ZIP includes the matching BotController native Metamod plugin; do not also install its CounterStrikeSharp provider, which exposes separate replay commands outside MatchZy practice mode.

Run `meta list`, `bc_status`, and `css_botreplay_status` in the server console to check the installation. BotController relies on game signatures, which may need updating after a CS2 update.

## Build and package

Needs the .NET 10 SDK, `curl`, `unzip`, Python 3, and `sha256sum`:

```sh
dotnet build -c Release -o dist
./package.sh v1.1.0
```

The packaging script downloads the pinned BotController Linux release, checks its checksum, and creates an installable ZIP under `dist/`. The plugin and native controller are distributed together so their ABI stays matched. The source copied under `vendor/` comes from [CS2-Bot-Controller v0.9.0](https://github.com/XBribo/CS2-Bot-Controller/tree/v0.9.0); the complete corresponding source for the native binary is available there.

## License

[AGPL-3.0](LICENSE). BotController is by XBribo and its contributors; its original licenses are included under `vendor/`.
