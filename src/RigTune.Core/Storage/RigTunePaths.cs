namespace RigTune.Core.Storage;

/// <summary>
/// Resolves every on-disk location RigTune uses. All state lives under
/// <c>%LOCALAPPDATA%\RigTune\</c> unless an alternate root is injected (tests).
/// </summary>
public sealed class RigTunePaths
{
    public RigTunePaths(string? root = null)
    {
        Root = string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RigTune")
            : Path.GetFullPath(root);
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string SnapshotsFile => Path.Combine(Root, "snapshots.json");
    public string HistoryFile => Path.Combine(Root, "history.json");
    public string GameModeFile => Path.Combine(Root, "gamemode.json");
    public string DiskLocksFile => Path.Combine(Root, "disk_locks.json");

    public void EnsureCreated() => Directory.CreateDirectory(Root);
}
