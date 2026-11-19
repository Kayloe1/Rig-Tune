using RigTune.Core.Services.GameMode;

namespace RigTune.Core.Services.DiskAnalyzer;

public enum PlayRecency
{
    Recent,
    Dormant60,
    Dormant90,
    NeverRecorded,
}

/// <summary>
/// Reads Steam's per-user <c>userdata\*\config\localconfig.vdf</c>
/// (<c>UserLocalConfigStore › Software › Valve › Steam › apps › {appid} › LastPlayed</c>, Unix seconds).
/// </summary>
public static class SteamPlayHistory
{
    public static IReadOnlyDictionary<string, DateTimeOffset> ParseLastPlayed(string localConfigVdf)
    {
        VdfNode root = VdfParser.Parse(localConfigVdf);
        VdfNode? apps = root["UserLocalConfigStore"]?["Software"]?["Valve"]?["Steam"]?["apps"] ?? FindApps(root);
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        if (apps is null)
        {
            return result;
        }

        foreach ((string appId, VdfNode node) in apps.Children)
        {
            if (long.TryParse(node.Get("LastPlayed"), out long unix) && unix > 0)
            {
                result[appId] = DateTimeOffset.FromUnixTimeSeconds(unix);
            }
        }

        return result;
    }

    /// <summary>Merges every Steam user on the PC (latest play wins).</summary>
    public static IReadOnlyDictionary<string, DateTimeOffset> ReadAll(string steamRoot)
    {
        var merged = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        string userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata))
        {
            return merged;
        }

        foreach (string user in Directory.EnumerateDirectories(userdata))
        {
            string file = Path.Combine(user, "config", "localconfig.vdf");
            if (!File.Exists(file))
            {
                continue;
            }

            foreach ((string app, DateTimeOffset played) in ParseLastPlayed(File.ReadAllText(file)))
            {
                if (!merged.TryGetValue(app, out DateTimeOffset existing) || played > existing)
                {
                    merged[app] = played;
                }
            }
        }

        return merged;
    }

    public static PlayRecency Classify(DateTimeOffset? lastPlayed, DateTimeOffset now) => lastPlayed switch
    {
        null => PlayRecency.NeverRecorded,
        { } p when now - p >= TimeSpan.FromDays(90) => PlayRecency.Dormant90,
        { } p when now - p >= TimeSpan.FromDays(60) => PlayRecency.Dormant60,
        _ => PlayRecency.Recent,
    };

    private static VdfNode? FindApps(VdfNode node)
    {
        foreach ((string key, VdfNode child) in node.Children)
        {
            if (key.Equals("apps", StringComparison.OrdinalIgnoreCase) && child.Children.Values.Any(c => c.Get("LastPlayed") is not null))
            {
                return child;
            }

            if (FindApps(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
