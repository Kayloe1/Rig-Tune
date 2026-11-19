using System.Text.Json;
using System.Text.Json.Nodes;
using RigTune.Core.Platform;

namespace RigTune.Core.Services.AppOptimizer;

/// <summary>
/// chrome://flags entries stored in a Chromium browser's <c>Local State</c>
/// (<c>browser.enabled_labs_experiments</c>: "flag@1" = Enabled, "flag@2" = Disabled, absent = Default).
/// Target format: <c>{localStatePath}|{flag}</c>. Writes are atomic and keep every other setting intact.
/// </summary>
public sealed class ChromiumFlagStateRunner : IStateRunner
{
    public const string Default = "Default";

    public string Kind => "chromium_flag";

    public static string Target(string localStatePath, string flag) => $"{localStatePath}|{flag}";

    public string? ReadState(string target)
    {
        (string path, string flag) = Split(target);
        JsonObject? root = Load(path);
        if (root is null)
        {
            return null;
        }

        foreach (string entry in Experiments(root).Select(n => n?.GetValue<string>() ?? string.Empty))
        {
            if (entry.StartsWith(flag + "@", StringComparison.Ordinal))
            {
                return entry[(flag.Length + 1)..] switch
                {
                    "1" => "Enabled",
                    "2" => "Disabled",
                    _ => Default,
                };
            }
        }

        return Default;
    }

    public void WriteState(string target, string state)
    {
        (string path, string flag) = Split(target);
        JsonObject root = Load(path) ?? throw new FileNotFoundException("Browser Local State not found.", path);
        JsonArray experiments = Experiments(root);
        for (int i = experiments.Count - 1; i >= 0; i--)
        {
            if (experiments[i]?.GetValue<string>()?.StartsWith(flag + "@", StringComparison.Ordinal) == true)
            {
                experiments.RemoveAt(i);
            }
        }

        if (string.Equals(state, "Enabled", StringComparison.OrdinalIgnoreCase))
        {
            experiments.Add($"{flag}@1");
        }
        else if (string.Equals(state, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            experiments.Add($"{flag}@2");
        }

        string temp = path + ".rigtune.tmp";
        File.WriteAllText(temp, root.ToJsonString());
        File.Replace(temp, path, path + ".rigtune.bak", ignoreMetadataErrors: true);
    }

    public string Describe(string target)
    {
        (string path, string flag) = Split(target);
        return $"Browser flag {flag} ({Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)) ?? path)})";
    }

    private static JsonArray Experiments(JsonObject root)
    {
        if (root["browser"] is not JsonObject browser)
        {
            browser = new JsonObject();
            root["browser"] = browser;
        }

        if (browser["enabled_labs_experiments"] is not JsonArray experiments)
        {
            experiments = new JsonArray();
            browser["enabled_labs_experiments"] = experiments;
        }

        return experiments;
    }

    private static JsonObject? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string Path, string Flag) Split(string target)
    {
        int bar = target.LastIndexOf('|');
        return bar > 0 ? (target[..bar], target[(bar + 1)..]) : throw new FormatException($"Invalid chromium flag target '{target}'.");
    }
}
