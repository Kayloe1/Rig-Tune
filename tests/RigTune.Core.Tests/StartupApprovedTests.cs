using Microsoft.Win32;
using RigTune.Core.Models;
using RigTune.Core.Services.SystemCare;
using RigTune.Core.Tests.Fakes;

namespace RigTune.Core.Tests;

public sealed class StartupApprovedTests : IDisposable
{
    private readonly SnapshotHarness _h = new();
    private readonly TempDir _userFolder = new();
    private readonly TempDir _commonFolder = new();

    private InMemoryRegistry Reg => (InMemoryRegistry)_h.Registry;

    [Fact]
    public void Encode_enabled_is_02_followed_by_zeros()
    {
        byte[] data = StartupApprovedFlags.Encode(enabled: true);

        Assert.Equal(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, data);
        Assert.True(StartupApprovedFlags.IsEnabled(data));
        Assert.Null(StartupApprovedFlags.DisabledAt(data));
    }

    [Fact]
    public void Encode_disabled_is_03_with_filetime_timestamp()
    {
        var when = new DateTimeOffset(2026, 11, 19, 21, 30, 0, TimeSpan.Zero);

        byte[] data = StartupApprovedFlags.Encode(enabled: false, when);

        Assert.Equal(12, data.Length);
        Assert.Equal(new byte[] { 0x03, 0, 0, 0 }, data[..4]);
        Assert.Equal(when.ToFileTime(), BitConverter.ToInt64(data, 4));
        Assert.False(StartupApprovedFlags.IsEnabled(data));
        Assert.Equal(when, StartupApprovedFlags.DisabledAt(data));
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x01 }, false)]
    [InlineData(new byte[0], true)]
    public void Decodes_task_manager_variants(byte[] data, bool enabled) =>
        Assert.Equal(enabled, StartupApprovedFlags.IsEnabled(data));

    [Fact]
    public void Missing_value_means_enabled() => Assert.True(StartupApprovedFlags.IsEnabled(null));

    [Theory]
    [InlineData("\"C:\\Program Files\\Discord\\Update.exe\" --processStart Discord.exe", "C:\\Program Files\\Discord\\Update.exe")]
    [InlineData("C:\\Program Files\\Steam\\steam.exe -silent", "C:\\Program Files\\Steam\\steam.exe")]
    [InlineData("rundll32.exe shell32.dll,Control_RunDLL", "rundll32.exe")]
    [InlineData("", null)]
    public void Extracts_executable_from_run_command(string command, string? expected) =>
        Assert.Equal(expected, StartupManager.ExtractExecutable(command));

    private StartupManager CreateManager() =>
        new(_h.Registry, _h.Snapshots, _h.Audit, _userFolder.Path, _commonFolder.Path, _h.Clock);

    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [Fact]
    public void Enumerates_run_keys_in_both_views_and_startup_folders()
    {
        Reg.WriteValue(new RegistryTarget(RegistryHiveName.HKCU, RegistryViewName.Registry64, Run, "Discord"), RegistryValueData.FromString("\"C:\\D\\Update.exe\""));
        Reg.WriteValue(new RegistryTarget(RegistryHiveName.HKLM, RegistryViewName.Registry64, Run, "SecurityHealth"), RegistryValueData.FromExpandString("%windir%\\system32\\SecurityHealthSystray.exe"));
        Reg.WriteValue(new RegistryTarget(RegistryHiveName.HKLM, RegistryViewName.Registry32, Run, "Razer Synapse"), RegistryValueData.FromString("C:\\Razer\\synapse.exe"));
        File.WriteAllText(Path.Combine(_userFolder.Path, "Spotify.lnk"), "x");
        File.WriteAllText(Path.Combine(_userFolder.Path, "desktop.ini"), "x");
        Reg.WriteValue(StartupManager.ApprovedTarget(StartupLocation.LocalMachineRun32, "Razer Synapse"),
            RegistryValueData.FromBinary(StartupApprovedFlags.Encode(false)));

        IReadOnlyList<StartupEntry> entries = CreateManager().Enumerate();

        Assert.Equal(new[] { "Discord", "Razer Synapse", "SecurityHealth", "Spotify.lnk" }, entries.Select(e => e.Name));
        Assert.False(entries.Single(e => e.Name == "Razer Synapse").IsEnabled);
        Assert.Equal(StartupLocation.LocalMachineRun32, entries.Single(e => e.Name == "Razer Synapse").Location);
        Assert.True(entries.Single(e => e.Name == "Discord").IsEnabled);
        Assert.Equal(StartupLocation.CurrentUserStartupFolder, entries.Single(e => e.Name == "Spotify.lnk").Location);
    }

    [Theory]
    [InlineData(StartupLocation.CurrentUserRun, RegistryHiveName.HKCU, "Run")]
    [InlineData(StartupLocation.LocalMachineRun, RegistryHiveName.HKLM, "Run")]
    [InlineData(StartupLocation.LocalMachineRun32, RegistryHiveName.HKLM, "Run32")]
    [InlineData(StartupLocation.CurrentUserStartupFolder, RegistryHiveName.HKCU, "StartupFolder")]
    [InlineData(StartupLocation.AllUsersStartupFolder, RegistryHiveName.HKLM, "StartupFolder")]
    public void Approved_keys_match_task_manager(StartupLocation location, RegistryHiveName hive, string leaf)
    {
        RegistryTarget t = StartupManager.ApprovedTarget(location, "App");

        Assert.Equal(hive, t.Hive);
        Assert.EndsWith($@"Explorer\StartupApproved\{leaf}", t.KeyPath);
        Assert.Equal(RegistryViewName.Registry64, t.View);
    }

    [Fact]
    public async Task Disabling_writes_only_the_approved_flag_keeps_the_entry_and_is_revertable()
    {
        var runValue = new RegistryTarget(RegistryHiveName.HKCU, RegistryViewName.Registry64, Run, "Discord");
        Reg.WriteValue(runValue, RegistryValueData.FromString("\"C:\\D\\Update.exe\""));
        StartupManager manager = CreateManager();
        StartupEntry entry = manager.Enumerate().Single();

        await manager.SetEnabledAsync(entry, enabled: false);

        RegistryValueData? flag = Reg.ReadValue(StartupManager.ApprovedTarget(StartupLocation.CurrentUserRun, "Discord"));
        Assert.Equal(RegistryValueKind.Binary, flag!.Kind);
        Assert.False(StartupApprovedFlags.IsEnabled(flag.GetBytes()));
        Assert.Equal(RegistryValueData.FromString("\"C:\\D\\Update.exe\""), Reg.ReadValue(runValue)); // untouched
        Assert.False(manager.Enumerate().Single().IsEnabled);
        Assert.Contains(await _h.Audit.GetAllAsync(), e => e.Action == AuditAction.StartupChanged && e.Summary.Contains("Disabled"));

        await _h.Snapshots.RevertTweakAsync(StartupManager.TweakId);
        Assert.Null(Reg.ReadValue(StartupManager.ApprovedTarget(StartupLocation.CurrentUserRun, "Discord")));
        Assert.True(manager.Enumerate().Single().IsEnabled);
    }

    public void Dispose()
    {
        _h.Dispose();
        _userFolder.Dispose();
        _commonFolder.Dispose();
    }
}
