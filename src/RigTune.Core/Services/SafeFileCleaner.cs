namespace RigTune.Core.Services;

/// <summary>
/// Measures and deletes the contents of cache folders/files: never follows junctions or symlinks,
/// skips files in use, and keeps the top-level folder itself.
/// </summary>
public static class SafeFileCleaner
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static (long Bytes, int Files) Measure(IEnumerable<string> targets)
    {
        long bytes = 0;
        int files = 0;
        foreach (FileInfo f in EnumerateFiles(targets))
        {
            bytes += f.Length;
            files++;
        }

        return (bytes, files);
    }

    public static (long Bytes, int Deleted, int Skipped) Delete(IEnumerable<string> targets)
    {
        long bytes = 0;
        int deleted = 0, skipped = 0;
        List<string> list = targets.ToList();
        foreach (FileInfo f in EnumerateFiles(list))
        {
            try
            {
                long length = f.Length;
                if (f.IsReadOnly)
                {
                    f.IsReadOnly = false;
                }

                f.Delete();
                bytes += length;
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        foreach (string dir in list.Where(Directory.Exists))
        {
            RemoveEmptySubdirectories(dir);
        }

        return (bytes, deleted, skipped);
    }

    private static IEnumerable<FileInfo> EnumerateFiles(IEnumerable<string> targets)
    {
        foreach (string target in targets)
        {
            if (File.Exists(target))
            {
                var info = new FileInfo(target);
                if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    yield return info;
                }

                continue;
            }

            if (!Directory.Exists(target) || new DirectoryInfo(target).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(target, "*", Options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                FileInfo? info = null;
                try
                {
                    info = new FileInfo(file);
                    if (!info.Exists)
                    {
                        info = null;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }

                if (info is not null)
                {
                    yield return info;
                }
            }
        }
    }

    private static void RemoveEmptySubdirectories(string root)
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(root, "*", Options).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
