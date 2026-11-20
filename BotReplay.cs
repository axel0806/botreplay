using System.Reflection;
using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Plugin;
using CounterStrikeSharp.API.Core.Plugin.Host;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.ValveConstants.Protobuf;

namespace BotReplay;

public class BotReplayPlugin : BasePlugin
{
    public override string ModuleName => "BotReplay";
    public override string ModuleVersion => "1.4.0";
    public override string ModuleAuthor => "local";
    public override string ModuleDescription => "Record your movement and replay it with an enemy bot in MatchZy practice.";

    private sealed record Track(ReplayTick[] Ticks, SubtickMove[] Subticks, ReplayCommandFrame[] Commands, byte[] History);
    private readonly List<int> _recording = new();
    private readonly HashSet<int> _playing = new();
    private List<Track> _tracks = new();
    private string? _map;
    private string? _recordingMap;
    private ulong _recordingSteamId;
    private nint _recordingPawn;
    private HashSet<int>? _beforeSpawn;
    private int _spawnTeam;
    private int _spawnTicks;
    private int _requesterSlot;
    private ulong _requesterSteamId;
    private bool _spawnCancelled;
    private int _ownedSlot = -1;
    private nint _ownedHandle;
    private int? _ownedUserId;
    private int _quotaBefore;
    private int? _quotaAfter;
    private string _lastPlaybackStatus = "idle";
    private int _recordTicks;
    private const int MaxTicks = 64 * 120;

    public override void Load(bool hotReload)
    {
        AddCommandListener("say", OnChat);
        AddCommandListener("say_team", OnChat);
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnMapStart>(_ => Reset());
        RegisterListener<Listeners.OnMapEnd>(Reset);
        RegisterEventHandler<EventGrenadeThrown>(OnGrenadeThrown);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        AddCommand("css_botreplay_status", "Show bot replay readiness", (_, cmd) =>
        {
            try { cmd.ReplyToCommand($"[BotReplay] MatchZy practice: {InPractice()}; native bridge: {BotController.RecordedTickCount(0) >= 0}; recording: {_recording.Count}; playing: {_playing.Count}; saved tracks: {_tracks.Count}; playback: {_lastPlaybackStatus}."); }
            catch (Exception ex) { cmd.ReplyToCommand($"[BotReplay] Native bridge unavailable: {ex.Message}"); }
        });
        foreach (var command in new[] { "botrec", "botstop", "botplay", "botcancel" })
            AddCommand("css_" + command, "Practice bot recording", (player, _) => Execute(player, command));
    }

    public override void Unload(bool hotReload) => Reset();

    // MatchZy 0.9.2 publishes no mode capability. Read its active plugin instance and
    // fail closed if it is unavailable, unloaded, or its mode field changes.
    private static object? MatchZy()
    {
        try
        {
            var manager = typeof(Application).GetField("_pluginManager", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(Application.Instance) as IPluginManager;
            return manager?.GetLoadedPlugins().SingleOrDefault(p => p.State == PluginState.Loaded && p.Plugin?.GetType().FullName == "MatchZy.MatchZy")?.Plugin;
        }
        catch { return null; }
    }
    private static bool InPractice() { var match = MatchZy(); return match?.GetType().GetField("isPractice", BindingFlags.Public | BindingFlags.Instance)?.GetValue(match) is true; }
    private static Dictionary<int, Dictionary<string, object>>? PracticeBots(object? match) =>
        match?.GetType().GetField("pracUsedBots", BindingFlags.Public | BindingFlags.Instance)?.GetValue(match) as Dictionary<int, Dictionary<string, object>>;

    private HookResult OnChat(CCSPlayerController? player, CommandInfo info)
    {
        var text = info.GetArg(1).Trim().Trim('"').ToLowerInvariant();
        if (text is ".botrec" or ".botstop" or ".botplay" or ".botcancel")
        { Execute(player, text[1..]); return HookResult.Handled; }
        return HookResult.Continue;
    }

    private void Execute(CCSPlayerController? p, string command)
    {
        if (p is not { IsValid: true, IsBot: false }) return;
        if (!InPractice()) { p.PrintToChat(" \x02[BotReplay] Only available in MatchZy practice (.prac)."); return; }
        if (!AdminManager.PlayerHasPermissions(p, "@css/root")) { p.PrintToChat(" \x02[BotReplay] Admin only."); return; }
        try
        {
            switch (command)
            {
                case "botrec": Record(p); break;
                case "botstop": StopRecording(p); break;
                case "botplay": Play(p); break;
                case "botcancel": StopAll(); p.PrintToChat(" \x04[BotReplay] Stopped."); break;
            }
        }
        catch (Exception ex)
        {
            StopAll();
            p.PrintToChat(" \x02[BotReplay] Bot controller unavailable; check server log.");
            Server.PrintToConsole($"[BotReplay] {ex}");
        }
    }

    private void Record(CCSPlayerController p)
    {
        if (p.TeamNum is not (2 or 3)) { p.PrintToChat(" \x02[BotReplay] Join T or CT first."); return; }
        if (_recording.Count > 0) { p.PrintToChat(" \x02[BotReplay] Already recording. Use .botstop."); return; }
        if (p.PlayerPawn is not { IsValid: true, Value.IsValid: true } || p.PlayerPawn.Value!.LifeState != 0)
        { p.PrintToChat(" \x02[BotReplay] You must be alive to record your movement."); return; }
        StopPlayback();
        var pawn = p.PlayerPawn.Value!;
        if (!BotController.SetReplayPawn(p.Slot, pawn.Handle) || !BotController.StartRecord(p.Slot))
        { p.PrintToChat(" \x02[BotReplay] Could not start recording; check controller status."); return; }
        _recording.Add(p.Slot); _recordingSteamId = p.SteamID; _recordingPawn = pawn.Handle;
        _recordingMap = Server.MapName; _recordTicks = 0;
        p.PrintToChat(" \x04[BotReplay] Recording your movement. Use .botstop (maximum 120 seconds).");
    }

    private void StopRecording(CCSPlayerController? p)
    {
        if (_recording.Count == 0) { p?.PrintToChat(" \x02[BotReplay] No recording in progress."); return; }
        var tracks = new List<Track>();
        foreach (int slot in _recording)
        {
            BotController.StopRecord(slot);
            var (ticks, subs, commands) = BotController.GetRecordedMotionExtended(slot);
            // The native recorder can emit a negative subtick fraction (e.g. -1/64),
            // while its replay loader requires each fraction to be in [0, 1).
            bool validSubticks = subs.All(s => float.IsFinite(s.When));
            if (validSubticks)
                for (int i = 0; i < subs.Length; i++)
                    subs[i].When = Math.Clamp(subs[i].When, 0f, MathF.BitDecrement(1f));
            if (validSubticks && ticks.Length > 0 && ticks.Length == commands.Length)
                tracks.Add(new Track(ticks, subs, commands, BotController.GetRecordedInputHistory(slot)));
        }
        _recording.Clear();
        if (tracks.Count > 0) { _tracks = tracks; _map = _recordingMap; }
        _recordingMap = null;
        var message = tracks.Count == 0 ? "[BotReplay] No movement was captured; the previous recording was kept." : "[BotReplay] Your movement was recorded, replacing the previous recording. Use .botplay.";
        if (p != null) p.PrintToChat(" \x04" + message); else Server.PrintToChatAll(" \x04" + message);
    }

    private void Play(CCSPlayerController p)
    {
        if (_recording.Count > 0) { p.PrintToChat(" \x02[BotReplay] Stop recording first with .botstop."); return; }
        if (_tracks.Count == 0 || _map != Server.MapName) { p.PrintToChat(" \x02[BotReplay] No recording for this map."); return; }
        if (p.TeamNum is not (2 or 3)) { p.PrintToChat(" \x02[BotReplay] Join T or CT first."); return; }
        if (_beforeSpawn != null) { p.PrintToChat(" \x02[BotReplay] A replay bot is already being created."); return; }
        StopPlayback();
        var match = MatchZy();
        var tracked = PracticeBots(match);
        var addBot = match?.GetType().GetMethod("AddBot", BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(CCSPlayerController), typeof(bool), typeof(bool) }, null);
        if (tracked == null || addBot == null)
        { p.PrintToChat(" \x02[BotReplay] MatchZy practice bot integration is unavailable."); return; }
        _beforeSpawn = tracked.Keys.ToHashSet();
        _spawnTeam = p.TeamNum == 2 ? 3 : 2;
        _requesterSlot = p.Slot; _requesterSteamId = p.SteamID;
        _spawnTicks = 0; _spawnCancelled = false;
        _lastPlaybackStatus = "waiting for MatchZy bot";
        _quotaBefore = ConVar.Find("bot_quota")?.GetPrimitiveValue<int>() ?? 0;
        _quotaAfter = null;
        if (addBot.Invoke(match, new object?[] { p, false, false }) is not true)
        { _beforeSpawn = null; p.PrintToChat(" \x02[BotReplay] MatchZy could not create a practice bot."); return; }
        p.PrintToChat(" \x04[BotReplay] Creating an enemy bot for playback...");
    }

    private void UpdateSpawn()
    {
        if (_beforeSpawn == null) return;
        if (_ownedSlot < 0)
        {
            var tracked = PracticeBots(MatchZy());
            if (tracked == null) { _spawnCancelled = true; }
            var added = tracked?.Where(entry => !_beforeSpawn.Contains(entry.Key) &&
                    entry.Value.TryGetValue("owner", out var owner) && owner is CCSPlayerController p &&
                    p.IsValid && p.Slot == _requesterSlot && p.SteamID == _requesterSteamId &&
                    entry.Value.TryGetValue("controller", out var value) && value is CCSPlayerController b &&
                    b.IsValid && b.IsBot && !b.IsHLTV && b.TeamNum == _spawnTeam)
                .Select(entry => (CCSPlayerController)entry.Value["controller"]).ToList() ?? new();
            if (added.Count > 1)
            {
                NotifyRequester("Could not identify the new bot; replay cancelled.");
                _beforeSpawn = null; return;
            }
            if (added.Count == 1)
            {
                var bot = added[0]; _ownedSlot = bot.Slot; _ownedHandle = bot.Handle; _ownedUserId = bot.UserId;
                _quotaAfter = ConVar.Find("bot_quota")?.GetPrimitiveValue<int>();
            }
        }
        if (_ownedSlot >= 0)
        {
            var bot = OwnedBot();
            if (bot == null) { _beforeSpawn = null; _ownedSlot = -1; NotifyRequester("Replay bot disconnected."); return; }
            if (_spawnCancelled || !InPractice()) { _beforeSpawn = null; RemoveOwnedBot(); return; }
            if (bot.TeamNum == _spawnTeam && _spawnTicks == 64 &&
                (bot.PlayerPawn is not { IsValid: true, Value.IsValid: true } || bot.PlayerPawn.Value!.LifeState != 0))
                bot.Respawn();
            if (bot.TeamNum == _spawnTeam && bot.PlayerPawn is { IsValid: true, Value.IsValid: true } && bot.PlayerPawn.Value!.LifeState == 0)
            {
                _beforeSpawn = null;
                var t = _tracks[0];
                var missing = PrepareGrenades(bot, t);
                if (!BotController.SetReplayPawn(bot.Slot, bot.PlayerPawn.Value.Handle) ||
                    !BotController.LoadReplay(bot.Slot, t.Ticks, t.Subticks, t.Commands, t.History) ||
                    !BotController.StartReplay(bot.Slot))
                { RemoveOwnedBot(); NotifyRequester("Could not start playback."); return; }
                _playing.Add(bot.Slot);
                NotifyRequester("Playing your recording on a new enemy bot. Use .botcancel to stop.");
                if (missing > 0) NotifyRequester("Could not equip every recorded grenade; check grenade carry limits.");
                return;
            }
        }
        if (++_spawnTicks >= 64 * 8)
        {
            _beforeSpawn = null; RemoveOwnedBot();
            if (!_spawnCancelled) NotifyRequester("Could not spawn a living enemy bot. Check server slots and team limits.");
        }
    }

    private static string? GrenadeForDef(int def, int team) => def switch
    {
        43 => "weapon_flashbang", 44 => "weapon_hegrenade", 45 => "weapon_smokegrenade",
        46 or 48 => team == 3 ? "weapon_incgrenade" : "weapon_molotov", 47 => "weapon_decoy", _ => null
    };

    private static bool HasWeapon(CCSPlayerController bot, string name)
    {
        var weapons = bot.PlayerPawn.Value?.WeaponServices?.MyWeapons;
        return weapons != null && weapons.Any(w => w.IsValid && w.Value is { IsValid: true } weapon && weapon.DesignerName == name);
    }

    private static int PrepareGrenades(CCSPlayerController bot, Track track)
    {
        int missing = 0;
        foreach (var name in track.Ticks.Select(t => GrenadeForDef(t.WeaponDefIndex, bot.TeamNum)).OfType<string>().Distinct())
            if (!HasWeapon(bot, name) && bot.GiveNamedItem(name) == nint.Zero) missing++;
        return missing;
    }

    // The native replay reproduces recorded attack and weapon-switch input. If the
    // server consumes grenades, replenish after a throw so later throws can replay.
    private HookResult OnGrenadeThrown(EventGrenadeThrown e, GameEventInfo info)
    {
        var bot = e.Userid;
        if (bot is not { IsValid: true, IsBot: true } || bot.Slot != _ownedSlot || bot.Handle != _ownedHandle ||
            !_playing.Contains(bot.Slot) || !InPractice()) return HookResult.Continue;
        var name = e.Weapon.StartsWith("weapon_", StringComparison.Ordinal) ? e.Weapon : "weapon_" + e.Weapon;
        if (name is not ("weapon_flashbang" or "weapon_hegrenade" or "weapon_smokegrenade" or "weapon_molotov" or "weapon_incgrenade" or "weapon_decoy"))
            return HookResult.Continue;
        var slot = bot.Slot; var handle = bot.Handle;
        AddTimer(0.1f, () =>
        {
            var current = OwnedBot();
            if (!InPractice() || current == null || current.Slot != slot || current.Handle != handle || !_playing.Contains(slot) || !BotController.IsReplaying(slot)) return;
            if (!HasWeapon(current, name)) current.GiveNamedItem(name);
        });
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath e, GameEventInfo info)
    {
        var victim = e.Userid;
        if (victim is not { IsValid: true, IsBot: true } || victim.Slot != _ownedSlot ||
            victim.Handle != _ownedHandle || victim.UserId != _ownedUserId || !_playing.Contains(victim.Slot))
            return HookResult.Continue;

        // Stop input immediately. Disconnect on the next frame, after the death event
        // has finished, and only if the slot still belongs to this replay bot.
        var slot = victim.Slot; var handle = victim.Handle; var userId = victim.UserId;
        BotController.StopReplay(slot);
        _playing.Remove(slot);
        _lastPlaybackStatus = "Replay ended because the bot died";
        Server.NextFrame(() =>
        {
            var bot = OwnedBot();
            if (bot != null && bot.Slot == slot && bot.Handle == handle && bot.UserId == userId)
                RemoveOwnedBot();
        });
        return HookResult.Continue;
    }

    private CCSPlayerController? OwnedBot()
    {
        var bot = _ownedSlot < 0 ? null : Utilities.GetPlayerFromSlot(_ownedSlot);
        return bot is { IsValid: true, IsBot: true } && bot.Handle == _ownedHandle && bot.UserId == _ownedUserId ? bot : null;
    }

    private void NotifyRequester(string message)
    {
        _lastPlaybackStatus = message;
        var p = Utilities.GetPlayerFromSlot(_requesterSlot);
        if (p is { IsValid: true, IsBot: false } && p.SteamID == _requesterSteamId)
            p.PrintToChat(" \x04[BotReplay] " + message);
    }

    private void RemoveOwnedBot()
    {
        var bot = OwnedBot();
        _ownedSlot = -1; _ownedHandle = 0; _ownedUserId = null;
        if (bot == null) return;
        var tracked = PracticeBots(MatchZy());
        if (bot.UserId is int userId && tracked != null && tracked.TryGetValue(userId, out var entry) &&
            entry.TryGetValue("controller", out var controller) && controller is CCSPlayerController recorded && recorded.Handle == bot.Handle)
            tracked.Remove(userId);
        var quota = ConVar.Find("bot_quota");
        var current = quota?.GetPrimitiveValue<int>();
        bot.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
        if (_quotaAfter is int after && current == after && after > _quotaBefore)
            quota!.SetValue(Math.Max(0, after - 1));
        _quotaAfter = null;
    }

    private void OnTick()
    {
        if (_recording.Count == 0 && _playing.Count == 0 && _beforeSpawn == null) return;
        try
        {
            if (!InPractice()) { StopAll(); UpdateSpawn(); return; }
            UpdateSpawn();
            if (_playing.Count > 0)
            {
                var bot = OwnedBot();
                if (bot == null || bot.PlayerPawn is not { IsValid: true, Value.IsValid: true } ||
                    bot.PlayerPawn.Value!.LifeState != 0 || !BotController.IsReplaying(_ownedSlot))
                    StopPlayback();
            }
            if (_recording.Count > 0)
            {
                var source = Utilities.GetPlayerFromSlot(_recording[0]);
                if (source is not { IsValid: true, IsBot: false } || source.SteamID != _recordingSteamId ||
                    source.PlayerPawn is not { IsValid: true, Value.IsValid: true } ||
                    source.PlayerPawn.Value!.Handle != _recordingPawn || source.PlayerPawn.Value.LifeState != 0 ||
                    ++_recordTicks >= MaxTicks)
                    StopRecording(null);
            }
        }
        catch (Exception ex) { Server.PrintToConsole($"[BotReplay] Tick error: {ex.Message}"); StopAll(); }
    }

    private void StopPlayback()
    {
        foreach (int slot in _playing) BotController.StopReplay(slot);
        _playing.Clear();
        if (_beforeSpawn != null) _spawnCancelled = true;
        else RemoveOwnedBot();
    }
    private void StopAll()
    {
        foreach (int slot in _recording) BotController.StopRecord(slot);
        _recording.Clear(); StopPlayback();
    }
    private void Reset() { try { StopAll(); } catch { _recording.Clear(); _playing.Clear(); } _tracks.Clear(); _map = null; _recordingMap = null; }
}
