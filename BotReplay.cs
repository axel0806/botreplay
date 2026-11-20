using System.Reflection;
using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Plugin;
using CounterStrikeSharp.API.Core.Plugin.Host;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;

namespace BotReplay;

public class BotReplayPlugin : BasePlugin
{
    public override string ModuleName => "BotReplay";
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "local";
    public override string ModuleDescription => "Record your movement and replay it with an enemy bot in MatchZy practice.";

    private sealed record Track(ReplayTick[] Ticks, SubtickMove[] Subticks, ReplayCommandFrame[] Commands, byte[] History);
    private readonly List<int> _recording = new();
    private readonly HashSet<int> _playing = new();
    private List<Track> _tracks = new();
    private string? _map;
    private ulong _recordingSteamId;
    private nint _recordingPawn;
    private int _recordTicks;
    private const int MaxTicks = 64 * 120;

    public override void Load(bool hotReload)
    {
        AddCommandListener("say", OnChat);
        AddCommandListener("say_team", OnChat);
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnMapStart>(_ => Reset());
        RegisterListener<Listeners.OnMapEnd>(Reset);
        AddCommand("css_botreplay_status", "Show bot replay readiness", (_, cmd) =>
        {
            try { cmd.ReplyToCommand($"[BotReplay] MatchZy practice: {InPractice()}; native bridge: {BotController.RecordedTickCount(0) >= 0}; recording: {_recording.Count}; playing: {_playing.Count}; saved tracks: {_tracks.Count}."); }
            catch (Exception ex) { cmd.ReplyToCommand($"[BotReplay] Native bridge unavailable: {ex.Message}"); }
        });
        foreach (var command in new[] { "botrec", "botstop", "botplay", "botcancel" })
            AddCommand("css_" + command, "Practice bot recording", (player, _) => Execute(player, command));
    }

    public override void Unload(bool hotReload) => Reset();

    // MatchZy 0.9.2 publishes no mode capability. Read its active plugin instance and
    // fail closed if it is unavailable, unloaded, or its mode field changes.
    private static bool InPractice()
    {
        try
        {
            var manager = typeof(Application).GetField("_pluginManager", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(Application.Instance) as IPluginManager;
            var match = manager?.GetLoadedPlugins().SingleOrDefault(p => p.State == PluginState.Loaded && p.Plugin?.GetType().FullName == "MatchZy.MatchZy")?.Plugin;
            return match?.GetType().GetField("isPractice", BindingFlags.Public | BindingFlags.Instance)?.GetValue(match) is true;
        }
        catch { return false; }
    }

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

    private static List<CCSPlayerController> Enemies(CCSPlayerController p) =>
        Utilities.GetPlayers().Where(b => b.IsValid && b.IsBot && b.TeamNum is 2 or 3 && b.TeamNum != p.TeamNum &&
            b.PlayerPawn is { IsValid: true, Value.IsValid: true } && b.PlayerPawn.Value!.LifeState == 0)
            .OrderBy(b => b.Slot).ToList();

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
        _tracks.Clear(); _map = Server.MapName; _recordTicks = 0;
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
            if (ticks.Length > 0 && ticks.Length == commands.Length)
                tracks.Add(new Track(ticks, subs, commands, BotController.GetRecordedInputHistory(slot)));
        }
        _recording.Clear(); _tracks = tracks;
        var message = tracks.Count == 0 ? "[BotReplay] No movement was captured; check server log." : "[BotReplay] Your movement was recorded. Use .botplay to replay it on an enemy bot.";
        if (p != null) p.PrintToChat(" \x04" + message); else Server.PrintToChatAll(" \x04" + message);
    }

    private void Play(CCSPlayerController p)
    {
        if (_recording.Count > 0) { p.PrintToChat(" \x02[BotReplay] Stop recording first with .botstop."); return; }
        if (_tracks.Count == 0 || _map != Server.MapName) { p.PrintToChat(" \x02[BotReplay] No recording for this map."); return; }
        if (p.TeamNum is not (2 or 3)) { p.PrintToChat(" \x02[BotReplay] Join T or CT first."); return; }
        var bots = Enemies(p);
        if (bots.Count < _tracks.Count) { p.PrintToChat($" \x02[BotReplay] Need {_tracks.Count} living enemy bots; found {bots.Count}."); return; }
        StopPlayback();
        for (int i = 0; i < _tracks.Count; i++)
        {
            var bot = bots[i]; var t = _tracks[i];
            if (!BotController.SetReplayPawn(bot.Slot, bot.PlayerPawn.Value!.Handle) ||
                !BotController.LoadReplay(bot.Slot, t.Ticks, t.Subticks, t.Commands, t.History) ||
                !BotController.StartReplay(bot.Slot))
            { StopPlayback(); p.PrintToChat(" \x02[BotReplay] Could not start every bot; playback stopped."); return; }
            _playing.Add(bot.Slot);
        }
        p.PrintToChat($" \x04[BotReplay] Playing {_playing.Count} tracks. Use .botcancel to stop.");
    }

    private void OnTick()
    {
        if (_recording.Count == 0 && _playing.Count == 0) return;
        try
        {
            if (!InPractice()) { StopAll(); return; }
            _playing.RemoveWhere(slot => !BotController.IsReplaying(slot));
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
    }
    private void StopAll()
    {
        foreach (int slot in _recording) BotController.StopRecord(slot);
        _recording.Clear(); StopPlayback();
    }
    private void Reset() { try { StopAll(); } catch { _recording.Clear(); _playing.Clear(); } _tracks.Clear(); _map = null; }
}
