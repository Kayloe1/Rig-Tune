using System.Text.Json;
using RigTune.Core.Models;
using RigTune.Core.Storage;

namespace RigTune.Core.Services;

/// <summary>
/// Export / import of a personal tweak profile (SPEC §5.7): the catalog tweaks that are currently active,
/// including the selected option for option-based tweaks. Importing re-applies them through the tweak engine
/// (snapshotted, Danger Zone rules enforced); interactive or read-only tweaks are skipped.
/// </summary>
public sealed class ProfileService
{
    private readonly TweakEngine _engine;
    private readonly SnapshotService _snapshots;
    private readonly AuditLogService _audit;
    private readonly TimeProvider _time;

    public ProfileService(TweakEngine engine, SnapshotService snapshots, AuditLogService audit, TimeProvider? time = null)
    {
        _engine = engine;
        _snapshots = snapshots;
        _audit = audit;
        _time = time ?? TimeProvider.System;
    }

    public async Task<TweakProfile> BuildAsync(CancellationToken cancellationToken = default)
    {
        var profile = new TweakProfile { ExportedAt = _time.GetUtcNow(), MachineName = Environment.MachineName };
        // Oldest first, so an import re-applies tweaks in the order they were originally applied.
        IEnumerable<string> ids = (await _snapshots.GetActiveAsync(cancellationToken).ConfigureAwait(false))
            .Reverse().Select(e => e.TweakId).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string id in ids)
        {
            if (_engine.Find(id) is not { } tweak || tweak.IsPreset || tweak.IsReadOnly)
            {
                continue;
            }

            string? option = null;
            foreach (TweakOption o in tweak.Options ?? new List<TweakOption>())
            {
                TweakStatusReport status = await _engine.GetStatusAsync(tweak, new TweakParameters { OptionId = o.Id }, cancellationToken).ConfigureAwait(false);
                if (status.Status == TweakStatus.Active)
                {
                    option = o.Id;
                    break;
                }
            }

            profile.Tweaks.Add(new ProfileTweak { Id = tweak.Id, Name = tweak.Name, OptionId = option });
        }

        return profile;
    }

    public async Task<TweakProfile> ExportAsync(string path, CancellationToken cancellationToken = default)
    {
        TweakProfile profile = await BuildAsync(cancellationToken).ConfigureAwait(false);
        await AtomicJsonFile.WriteAsync(path, profile, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(AuditAction.Info, $"Exported tweak profile ({profile.Tweaks.Count} tweaks)", "profile.export", path,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        return profile;
    }

    public static TweakProfile Read(string path)
    {
        TweakProfile profile = JsonSerializer.Deserialize<TweakProfile>(File.ReadAllText(path), AtomicJsonFile.DefaultOptions)
                               ?? throw new InvalidDataException("The profile file is empty.");
        if (profile.SchemaVersion != 1 || !string.Equals(profile.Application, "RigTune", StringComparison.Ordinal))
        {
            throw new InvalidDataException("This is not a RigTune tweak profile (schema version 1).");
        }

        return profile;
    }

    public async Task<ProfileImportResult> ImportAsync(TweakProfile profile, CancellationToken cancellationToken = default)
    {
        var items = new List<ProfileImportItem>();
        foreach (ProfileTweak entry in profile.Tweaks)
        {
            if (_engine.Find(entry.Id) is not { } tweak)
            {
                items.Add(new ProfileImportItem(entry.Id, entry.Name ?? entry.Id, false, "Not in this version's catalog."));
                continue;
            }

            if (tweak.IsPreset || tweak.IsReadOnly || tweak.Interactive is not null && tweak.Interactive != "power_plan")
            {
                items.Add(new ProfileImportItem(tweak.Id, tweak.Name, false, "Needs device/selection input — apply it from the Tweaks page."));
                continue;
            }

            var parameters = new TweakParameters
            {
                OptionId = tweak.Options?.Any(o => o.Id == entry.OptionId) == true ? entry.OptionId : null,
            };
            TweakApplyResult r = await _engine.ApplyAsync(tweak, parameters, cancellationToken).ConfigureAwait(false);
            items.Add(new ProfileImportItem(tweak.Id, tweak.Name, r.Success, r.Message));
        }

        var result = new ProfileImportResult(items);
        await _audit.LogAsync(AuditAction.Info, $"Imported tweak profile: {result.Applied} applied, {result.Skipped} skipped", "profile.import",
            success: true, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        return result;
    }
}
