using System.Runtime.InteropServices;
using Microsoft.VisualBasic.FileIO;
using RigTune.Core.Models;

namespace RigTune.Core.Services.DiskAnalyzer;

/// <summary>The only way Disk Analyzer removes anything: the Windows Recycle Bin (abstracted for tests).</summary>
public interface IRecycleBin
{
    void SendFileToRecycleBin(string path);

    void SendDirectoryToRecycleBin(string path);

    (long Bytes, long Items) QuerySize();

    bool Empty();
}

/// <summary>
/// <c>Microsoft.VisualBasic.FileIO.FileSystem.Delete*(…, RecycleOption.SendToRecycleBin)</c> (SPEC §3.3.3);
/// never <c>Directory.Delete</c>.
/// </summary>
public sealed class WindowsRecycleBin : IRecycleBin
{
    private const uint SHERB_NOCONFIRMATION = 0x1;
    private const uint SHERB_NOPROGRESSUI = 0x2;
    private const uint SHERB_NOSOUND = 0x4;

    public void SendFileToRecycleBin(string path) =>
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);

    public void SendDirectoryToRecycleBin(string path) =>
        FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);

    public (long Bytes, long Items) QuerySize()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    public bool Empty() => SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND) == 0;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}

/// <summary>A user-confirmed request: the UI must show the exact path and size before setting <see cref="Confirmed"/>.</summary>
public sealed record DeletionRequest(string Path, long DisplayedBytes, bool Confirmed);

/// <summary>
/// Guarded deletion for Disk Analyzer and Space Advisor: requires explicit confirmation, re-checks hard
/// protection and Important locks at the moment of deletion, and only ever uses the Recycle Bin.
/// </summary>
public sealed class DiskDeletionService
{
    private readonly PathGuard _guard;
    private readonly DiskLockService _locks;
    private readonly IRecycleBin _recycleBin;
    private readonly AuditLogService _audit;
    private readonly DiskAnalyzerService? _analyzer;

    public DiskDeletionService(PathGuard guard, DiskLockService locks, IRecycleBin recycleBin, AuditLogService audit,
        DiskAnalyzerService? analyzer = null)
    {
        _guard = guard;
        _locks = locks;
        _recycleBin = recycleBin;
        _audit = audit;
        _analyzer = analyzer;
    }

    public async Task<DeletionCheck> CheckAsync(string path, CancellationToken cancellationToken = default) =>
        _guard.Evaluate(path, await _locks.GetLocksAsync(cancellationToken).ConfigureAwait(false));

    public async Task<DeletionResult> RecycleAsync(DeletionRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Confirmed)
        {
            return new DeletionResult(request.Path, false, 0, "Deletion needs explicit confirmation of the path and size.");
        }

        DeletionCheck check = await CheckAsync(request.Path, cancellationToken).ConfigureAwait(false);
        if (!check.Allowed)
        {
            await _audit.LogAsync(AuditAction.Info, $"Blocked deletion of {request.Path}", "disk.delete", request.Path, check.Reason,
                success: false, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return new DeletionResult(request.Path, false, 0, check.Reason);
        }

        DeletionResult result;
        try
        {
            if (File.Exists(request.Path))
            {
                long size = new FileInfo(request.Path).Length;
                await Task.Run(() => _recycleBin.SendFileToRecycleBin(request.Path), cancellationToken).ConfigureAwait(false);
                result = new DeletionResult(request.Path, true, size, "Moved to the Recycle Bin.");
            }
            else if (Directory.Exists(request.Path))
            {
                if (new DirectoryInfo(request.Path).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return new DeletionResult(request.Path, false, 0, "This is a junction/symbolic link; RigTune does not delete link targets.");
                }

                await Task.Run(() => _recycleBin.SendDirectoryToRecycleBin(request.Path), cancellationToken).ConfigureAwait(false);
                result = new DeletionResult(request.Path, true, request.DisplayedBytes, "Moved to the Recycle Bin.");
            }
            else
            {
                result = new DeletionResult(request.Path, false, 0, "The item no longer exists.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            result = new DeletionResult(request.Path, false, 0, ex.Message);
        }

        if (result.Success)
        {
            _analyzer?.RemoveFromCache(request.Path);
        }

        await _audit.LogAsync(AuditAction.Cleaned,
            result.Success ? $"Recycled {request.Path} ({SystemCare.CleanupEngine.FormatBytes(result.BytesRecycled)})" : $"Could not recycle {request.Path}",
            "disk.delete", request.Path, result.Message, result.Success, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    /// <summary>Recycles every item inside a folder (the folder itself stays) — used for cache/temp zones.</summary>
    public async Task<DeletionResult> RecycleContentsAsync(DeletionRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Confirmed)
        {
            return new DeletionResult(request.Path, false, 0, "Deletion needs explicit confirmation of the path and size.");
        }

        if (!Directory.Exists(request.Path))
        {
            return new DeletionResult(request.Path, false, 0, "The folder does not exist.");
        }

        long bytes = 0;
        int ok = 0, failed = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(request.Path))
        {
            long size = File.Exists(entry) ? new FileInfo(entry).Length : 0;
            DeletionResult r = await RecycleAsync(new DeletionRequest(entry, size, true), cancellationToken).ConfigureAwait(false);
            if (r.Success)
            {
                ok++;
                bytes += r.BytesRecycled;
            }
            else
            {
                failed++;
            }
        }

        return new DeletionResult(request.Path, failed == 0 || ok > 0, bytes,
            $"{ok} item(s) moved to the Recycle Bin{(failed > 0 ? $", {failed} skipped (in use or protected)" : string.Empty)}.");
    }
}
