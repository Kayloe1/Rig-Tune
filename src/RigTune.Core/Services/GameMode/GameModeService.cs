using RigTune.Core.Models;
using RigTune.Core.Services.AppOptimizer;
using RigTune.Core.Storage;

namespace RigTune.Core.Services.GameMode;

/// <summary>
/// Game Mode (SPEC §3.3.2, §5.5). On game start: game → High priority; App Optimizer "While You Play"
/// apps → Below Normal + EcoQoS (Thread Director moves them to the E-cores) + low I/O; background RAM
/// trimmed only under memory pressure; optional power-plan switch. Every original value is written to
/// <c>gamemode.json</c> BEFORE it is changed, so a crash is recovered on the next start, and everything is
/// restored exactly on game exit, manual Stop or app exit.
/// </summary>
public sealed class GameModeService : IDisposable
{
    private readonly IGameModePlatform _platform;
    private readonly JsonDocumentStore<GameModeDocument> _store;
    private readonly SettingsService _settings;
    private readonly AuditLogService _audit;
    private readonly Func<CancellationToken, Task<IReadOnlyList<(AppProfile App, WhilePlayingRule Rule)>>> _rules;
    private readonly Func<Task>? _onSessionStart;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeProvider _time;
    private ProcessStartWatcher? _watcher;
    private Timer? _exitPoller;
    private GameMatcher _matcher = new(Array.Empty<DiscoveredGame>(), Array.Empty<string>(), Array.Empty<string>());

    public GameModeService(IGameModePlatform platform, JsonDocumentStore<GameModeDocument> store, SettingsService settings,
        AuditLogService audit, Func<CancellationToken, Task<IReadOnlyList<(AppProfile App, WhilePlayingRule Rule)>>> rules,
        Func<Task>? onSessionStart = null, TimeProvider? time = null)
    {
        _platform = platform;
        _store = store;
        _settings = settings;
        _audit = audit;
        _rules = rules;
        _onSessionStart = onSessionStart;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Production wiring: rules come from the App Optimizer.</summary>
    /// <remarks>
    /// <paramref name="store"/> must be the same instance the App Optimizer uses for gamemode.json, so the
    /// two never overwrite each other's cached copy.
    /// </remarks>
    public static GameModeService Create(IGameModePlatform platform, JsonDocumentStore<GameModeDocument> store, SettingsService settings,
        AuditLogService audit, AppOptimizerService apps, RefreshRateWatchdogService watchdog) =>
        new(platform, store, settings, audit,
            async ct =>
            {
                var list = new List<(AppProfile, WhilePlayingRule)>();
                foreach (AppProfile app in apps.Apps)
                {
                    if (await apps.GetWhilePlayingAsync(app, ct).ConfigureAwait(false) is { Enabled: true } rule)
                    {
                        list.Add((app, rule));
                    }
                }

                return list;
            },
            async () => await watchdog.CheckAsync("Game Mode start").ConfigureAwait(false));

    public event EventHandler<GameSession?>? SessionChanged;

    public GameSession? ActiveSession { get; private set; }

    public GameModeState State => ActiveSession is null ? GameModeState.Idle : GameModeState.Active;

    public JsonDocumentStore<GameModeDocument> Store => _store;

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Recovers a stale session (crash / power loss) and starts process watching.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        GameModeDocument doc = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (doc.ActiveSession is not null)
        {
            await RecoverStaleSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        await ReloadMatcherAsync(cancellationToken).ConfigureAwait(false);
        if ((await _settings.GetAsync(cancellationToken).ConfigureAwait(false)).GameModeAutoDetect)
        {
            StartWatching();
        }
    }

    public void StartWatching()
    {
        if (_watcher is not null)
        {
            return;
        }

        _watcher = new ProcessStartWatcher(_platform);
        _watcher.ProcessStarted += (_, pid) => _ = OnProcessStartedAsync(pid);
        _watcher.Start();
    }

    public void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    public bool IsWatching => _watcher is not null;

    public bool WatcherUsesWmi => _watcher?.UsingWmi == true;

    public async Task ReloadMatcherAsync(CancellationToken cancellationToken = default)
    {
        GameModeDocument doc = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        _matcher = new GameMatcher(doc.DiscoveredGames, doc.CustomExeAllowlist, doc.ExclusionList);
    }

    /// <summary>Restores a session left in gamemode.json by a crash, then clears it.</summary>
    public async Task<GameSessionResult> RecoverStaleSessionAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GameModeDocument doc = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (doc.ActiveSession is not { } stale)
            {
                return new GameSessionResult(true, "No stale session.");
            }

            GameSessionResult result = await RestoreAsync(stale).ConfigureAwait(false);
            await _audit.LogAsync(AuditAction.Repaired, "Recovered an unfinished Game Mode session", "gamemode.session",
                stale.GameExe, result.Message, result.Failed == 0, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ------------------------------------------------------------------ start

    private async Task OnProcessStartedAsync(int pid)
    {
        try
        {
            if (ActiveSession is not null)
            {
                await ThrottleLateStarterAsync(pid).ConfigureAwait(false);
                return;
            }

            RunningProcess? process = _platform.GetProcess(pid);
            if (process is null || _matcher.Match(process.Name, process.Path) is not { } title)
            {
                return;
            }

            await StartAsync(process, title, manual: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await _audit.LogAsync(AuditAction.ApplyFailed, "Game Mode could not start", "gamemode.session", details: ex.Message,
                success: false).ConfigureAwait(false);
        }
    }

    /// <summary>Manual Start: uses the foreground app as the game when it isn't RigTune/Explorer.</summary>
    public Task<GameSessionResult> StartManualAsync(CancellationToken cancellationToken = default)
    {
        RunningProcess? foreground = _platform.GetForegroundProcessId() is { } pid ? _platform.GetProcess(pid) : null;
        if (foreground is not null && (foreground.Pid == Environment.ProcessId ||
                                       foreground.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)))
        {
            foreground = null;
        }

        return StartAsync(foreground, foreground is null ? null : Path.GetFileNameWithoutExtension(foreground.Name), manual: true, cancellationToken);
    }

    public async Task<GameSessionResult> StartAsync(RunningProcess? game, string? title, bool manual,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ActiveSession is not null)
            {
                return new GameSessionResult(false, "A Game Mode session is already active.");
            }

            AppSettings settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            var session = new GameSession
            {
                StartedAtUtc = _time.GetUtcNow(),
                IsManual = manual,
                GamePid = game?.Pid,
                GameExe = game?.Name,
                GameName = title ?? game?.Name,
                GameStartTimeUtc = game?.StartTimeUtc,
                GameOriginalPriorityClass = game is null ? null : _platform.GetPriorityClass(game.Pid),
            };

            if (settings.SwitchPowerPlanDuringGames)
            {
                session.OriginalPowerPlan = _platform.GetActivePowerPlan();
                session.AppliedPowerPlan = settings.GamingPowerPlan;
            }

            // Capture originals of every process we will touch, and persist BEFORE changing anything.
            IReadOnlyList<(AppProfile App, WhilePlayingRule Rule)> rules = await _rules(cancellationToken).ConfigureAwait(false);
            var targets = new List<(ThrottledProcess Record, WhilePlayingRule Rule)>();
            foreach (RunningProcess p in _platform.GetProcesses())
            {
                if (p.Pid == game?.Pid || p.Pid == Environment.ProcessId)
                {
                    continue;
                }

                (AppProfile App, WhilePlayingRule Rule)? match = MatchRule(rules, p.Name);
                if (match is { } m && _platform.GetPriorityClass(p.Pid) is { } original)
                {
                    targets.Add((new ThrottledProcess
                    {
                        Pid = p.Pid,
                        Name = p.Name,
                        AppId = m.App.Id,
                        StartTimeUtc = p.StartTimeUtc,
                        OriginalPriorityClass = original,
                        OriginalThrottle = _platform.GetPowerThrottling(p.Pid),
                        OriginalIoPriority = _platform.GetIoPriority(p.Pid),
                    }, m.Rule));
                }
            }

            session.Throttled = targets.Select(t => t.Record).ToList();
            await PersistAsync(session, cancellationToken).ConfigureAwait(false);
            ActiveSession = session;

            // Apply.
            if (game is not null && session.GameOriginalPriorityClass is not null)
            {
                _platform.SetPriorityClass(game.Pid, PriorityClasses.High);
            }

            foreach ((ThrottledProcess record, WhilePlayingRule rule) in targets)
            {
                Throttle(record, rule);
            }

            bool trimmed = false;
            if (settings.RamTrimEnabled && _platform.GetMemoryLoadPercent() >= settings.RamTrimThresholdPercent)
            {
                foreach (ThrottledProcess record in session.Throttled)
                {
                    record.Trimmed = _platform.TrimWorkingSet(record.Pid);
                    trimmed |= record.Trimmed;
                }
            }

            if (session.AppliedPowerPlan is { } plan && !_platform.SetActivePowerPlan(plan))
            {
                session.AppliedPowerPlan = null; // plan not present on this PC
            }

            await PersistAsync(session, cancellationToken).ConfigureAwait(false);
            StartExitPolling();
            SessionChanged?.Invoke(this, session);

            string summary = $"Game Mode on{(session.GameName is null ? " (manual)" : $" for {session.GameName}")}: " +
                             $"{session.Throttled.Count} background process(es) throttled" +
                             (trimmed ? ", RAM trimmed (memory pressure)" : string.Empty) +
                             (session.AppliedPowerPlan is not null ? ", power plan switched" : string.Empty) + ".";
            await _audit.LogAsync(AuditAction.Applied, summary, "gamemode.session", session.GameExe,
                string.Join(", ", session.Throttled.Select(t => $"{t.Name}({t.Pid})")), cancellationToken: CancellationToken.None).ConfigureAwait(false);
            if (_onSessionStart is not null)
            {
                _ = _onSessionStart();
            }

            return new GameSessionResult(true, summary, session.Throttled.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Throttle(ThrottledProcess record, WhilePlayingRule rule)
    {
        _platform.SetPriorityClass(record.Pid, ParsePriority(rule.CpuPriority));
        if (string.Equals(rule.Mode, "EcoQoS", StringComparison.OrdinalIgnoreCase))
        {
            record.EcoQosApplied = _platform.SetPowerThrottling(record.Pid, ThrottleState.EcoQoS);
        }

        if (string.Equals(rule.IoPriority, "Low", StringComparison.OrdinalIgnoreCase) && record.OriginalIoPriority is not null)
        {
            record.IoPriorityLowered = _platform.SetIoPriority(record.Pid, IoPriorities.Low);
        }
    }

    /// <summary>Background apps launched mid-session get the same treatment.</summary>
    private async Task ThrottleLateStarterAsync(int pid)
    {
        RunningProcess? p = _platform.GetProcess(pid);
        if (p is null || ActiveSession is null || p.Pid == ActiveSession.GamePid)
        {
            return;
        }

        IReadOnlyList<(AppProfile App, WhilePlayingRule Rule)> rules = await _rules(CancellationToken.None).ConfigureAwait(false);
        if (MatchRule(rules, p.Name) is not { } m || _platform.GetPriorityClass(p.Pid) is not { } original)
        {
            return;
        }

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ActiveSession is not { } session || session.Throttled.Any(t => t.Pid == p.Pid))
            {
                return;
            }

            var record = new ThrottledProcess
            {
                Pid = p.Pid, Name = p.Name, AppId = m.App.Id, StartTimeUtc = p.StartTimeUtc, OriginalPriorityClass = original,
                OriginalThrottle = _platform.GetPowerThrottling(p.Pid), OriginalIoPriority = _platform.GetIoPriority(p.Pid),
            };
            session.Throttled.Add(record);
            await PersistAsync(session, CancellationToken.None).ConfigureAwait(false);
            Throttle(record, m.Rule);
            await PersistAsync(session, CancellationToken.None).ConfigureAwait(false);
            SessionChanged?.Invoke(this, session);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ------------------------------------------------------------------ stop / restore

    public async Task<GameSessionResult> StopAsync(string reason = "Stopped", CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ActiveSession is not { } session)
            {
                return new GameSessionResult(true, "Game Mode is not active.");
            }

            GameSessionResult result = await RestoreAsync(session).ConfigureAwait(false);
            await _audit.LogAsync(AuditAction.Reverted, $"Game Mode off ({reason}): {result.Message}", "gamemode.session",
                session.GameExe, success: result.Failed == 0, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Restores exactly what the session changed (only for processes that are still the same instance).</summary>
    private async Task<GameSessionResult> RestoreAsync(GameSession session)
    {
        StopExitPolling();
        int restored = 0, failed = 0, gone = 0;
        foreach (ThrottledProcess t in session.Throttled)
        {
            if (!IsSameInstance(t.Pid, t.StartTimeUtc))
            {
                gone++;
                continue;
            }

            bool ok = _platform.SetPriorityClass(t.Pid, t.OriginalPriorityClass);
            if (t.EcoQosApplied)
            {
                ok &= _platform.SetPowerThrottling(t.Pid, t.OriginalThrottle ?? ThrottleState.Default);
            }

            if (t.IoPriorityLowered && t.OriginalIoPriority is { } io)
            {
                ok &= _platform.SetIoPriority(t.Pid, io);
            }

            if (ok)
            {
                restored++;
            }
            else
            {
                failed++;
            }
        }

        if (session.GamePid is { } gamePid && session.GameOriginalPriorityClass is { } gamePriority &&
            IsSameInstance(gamePid, session.GameStartTimeUtc))
        {
            if (!_platform.SetPriorityClass(gamePid, gamePriority))
            {
                failed++;
            }
        }

        if (session.OriginalPowerPlan is { } plan && session.AppliedPowerPlan is not null)
        {
            if (!_platform.SetActivePowerPlan(plan))
            {
                failed++;
            }
        }

        await _store.UpdateAsync(doc => doc.ActiveSession = null, CancellationToken.None).ConfigureAwait(false);
        ActiveSession = null;
        SessionChanged?.Invoke(this, null);
        return new GameSessionResult(failed == 0,
            $"Restored {restored} process(es){(gone > 0 ? $", {gone} already exited" : string.Empty)}" +
            (session.OriginalPowerPlan is not null && session.AppliedPowerPlan is not null ? ", power plan restored" : string.Empty) +
            (failed > 0 ? $", {failed} could not be restored" : string.Empty) + ".", restored, failed);
    }

    private bool IsSameInstance(int pid, DateTime? startTimeUtc)
    {
        RunningProcess? p = _platform.GetProcess(pid);
        return p is not null && (startTimeUtc is null || p.StartTimeUtc is null ||
                                 Math.Abs((p.StartTimeUtc.Value - startTimeUtc.Value).TotalSeconds) < 2);
    }

    private void StartExitPolling()
    {
        StopExitPolling();
        _exitPoller = new Timer(_ =>
        {
            GameSession? s = ActiveSession;
            if (s is { IsManual: false, GamePid: { } pid } && !IsSameInstance(pid, s.GameStartTimeUtc))
            {
                _ = StopAsync("game exited");
            }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void StopExitPolling()
    {
        _exitPoller?.Dispose();
        _exitPoller = null;
    }

    private Task PersistAsync(GameSession session, CancellationToken cancellationToken) =>
        _store.UpdateAsync(doc => doc.ActiveSession = session, cancellationToken);

    // ------------------------------------------------------------------ lists

    public async Task UpdateListsAsync(Action<GameModeDocument> mutate, CancellationToken cancellationToken = default)
    {
        await _store.UpdateAsync(mutate, cancellationToken).ConfigureAwait(false);
        await ReloadMatcherAsync(cancellationToken).ConfigureAwait(false);
    }

    public static (AppProfile App, WhilePlayingRule Rule)? MatchRule(IReadOnlyList<(AppProfile App, WhilePlayingRule Rule)> rules, string processName)
    {
        foreach ((AppProfile app, WhilePlayingRule rule) in rules)
        {
            if (app.Processes.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(Path.GetFileNameWithoutExtension(p), Path.GetFileNameWithoutExtension(processName), StringComparison.OrdinalIgnoreCase)))
            {
                return (app, rule);
            }
        }

        return null;
    }

    public static int ParsePriority(string? value) => value?.Replace(" ", string.Empty).ToLowerInvariant() switch
    {
        "idle" => PriorityClasses.Idle,
        "normal" => PriorityClasses.Normal,
        _ => PriorityClasses.BelowNormal,
    };

    public void Dispose()
    {
        StopWatching();
        StopExitPolling();
    }
}
