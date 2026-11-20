# BotReplay

A [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) plugin for CS2 that records your movement during [MatchZy](https://github.com/shobhit-pathak/MatchZy) practice and replays it on an enemy bot.

## Usage

Join T or CT and enter MatchZy practice with `.prac`. These commands require the `@css/root` permission:

- `.botrec` starts recording your movement and grenade inputs. You must be alive.
- `.botstop` finishes the recording.
- `.botplay` spawns an enemy bot and replays the recording from your recorded position, including grenade throws. If the bot dies, playback ends immediately. The bot is removed after playback or cancellation.
- `.botcancel` stops recording or playback.

Recordings are held in memory for the current map, up to two minutes. They are lost when the map changes, the plugin reloads or the server restarts. A completed recording replaces the previous one; cancelling or capturing no input keeps the previous recording. Recording ends if the player dies or disconnects. The bot receives the recorded grenade types and replays recorded weapon switches and throw inputs. Carry limits or changed surroundings may affect grenade playback. Fights and other interactions may also change the outcome.

The plugin reads the loaded MatchZy instance's practice flag and requests its practice bot through MatchZy's own bot manager. It refuses commands and stops active recording or replay when practice ends. If the needed MatchZy state is unavailable, it fails closed. This uses MatchZy 0.9.x internals and may need updating if they change.

## Install

Requires a Linux CS2 dedicated server with Metamod:Source, CounterStrikeSharp 1.0.379 or newer (.NET 10), and MatchZy. Download the latest `BotReplay-v*.zip` from [Releases](https://github.com/axel0806/botreplay/releases), extract it into the server's `game/csgo/` directory, and restart the server. The ZIP includes the matching BotController native Metamod plugin; do not also install its CounterStrikeSharp provider, which exposes separate replay commands outside MatchZy practice mode.

Run `meta list`, `bc_status`, and `css_botreplay_status` in the server console to check the installation. BotController relies on game signatures, which may need updating after a CS2 update.

## Build and package

Needs the .NET 10 SDK, `curl`, `unzip`, Python 3, and `sha256sum`:

```sh
dotnet build -c Release -o dist
./package.sh v1.4.0
```

The packaging script downloads the pinned BotController Linux release, checks its checksum, and creates an installable ZIP under `dist/`. The plugin and native controller are distributed together so their ABI stays matched. The source copied under `vendor/` comes from [CS2-Bot-Controller v0.9.0](https://github.com/XBribo/CS2-Bot-Controller/tree/v0.9.0); the complete corresponding source for the native binary is available there.

## License

[AGPL-3.0](LICENSE). BotController is by XBribo and its contributors; its original licenses are included under `vendor/`.
