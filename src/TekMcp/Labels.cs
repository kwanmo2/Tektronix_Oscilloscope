using System.Text.Json;
using System.Text.Json.Nodes;
using TekCapture;

namespace TekMcp;

/// <summary>Shares channel labels and the save folder with the TekCapture app
/// (%APPDATA%\TekCapture\settings.json → "Labels": {"CH1": "VIN", ...}).</summary>
public static class AppSettings
{
    public static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TekCapture", "settings.json");

    public static string DefaultFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TekCapture");

    private static JsonObject Load()
    {
        try
        {
            if (File.Exists(Path) && JsonNode.Parse(File.ReadAllText(Path)) is JsonObject o) return o;
        }
        catch { /* corrupt → treat as empty */ }
        return new JsonObject();
    }

    public static Dictionary<string, string> Labels
    {
        get
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Load()["Labels"] is JsonObject lbl)
                foreach (var (k, v) in lbl)
                    if (v is JsonValue jv && jv.TryGetValue(out string? s)) d[k] = s.Trim();
            return d;
        }
    }

    /// <summary>Trace colors chosen in the app ("Colors": {"CH1": "#FFE600"}).</summary>
    public static Dictionary<string, System.Drawing.Color> Colors
    {
        get
        {
            var d = new Dictionary<string, System.Drawing.Color>(StringComparer.OrdinalIgnoreCase);
            if (Load()["Colors"] is JsonObject o)
                foreach (var (k, v) in o)
                    if (v is JsonValue jv && jv.TryGetValue(out string? s) && TryColor(s, out var c)) d[k] = c;
            return d;
        }
    }

    private static bool TryColor(string? s, out System.Drawing.Color c)
    {
        c = default;
        try { if (!string.IsNullOrWhiteSpace(s)) c = System.Drawing.ColorTranslator.FromHtml(s.Trim()); }
        catch { return false; }
        return !c.IsEmpty;
    }

    /// <summary>Save folder configured in the app (falls back to Documents\TekCapture).</summary>
    public static string Folder
    {
        get
        {
            var f = Load()["Folder"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(f) ? DefaultFolder : f.Trim();
        }
    }

    public static string? Resource => Load()["Resource"]?.GetValue<string>();

    /// <summary>Merges labels into settings.json, keeping every other setting.</summary>
    public static void SaveLabels(IReadOnlyDictionary<string, string> labels)
    {
        var root = Load();
        if (root["Labels"] is not JsonObject lbl)
        {
            lbl = new JsonObject();
            root["Labels"] = lbl;
        }
        foreach (var (k, v) in labels) lbl[k.ToUpperInvariant()] = v.Trim();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }
}

public static class Names
{
    /// <summary>"VIN [CH1]" when labelled, otherwise "CH1".</summary>
    public static string Of(Waveform w) => string.IsNullOrWhiteSpace(w.Label) ? w.Source : $"{w.Label} [{w.Source}]";

    /// <summary>Short name for running text: label if present, else source.</summary>
    public static string Short(Waveform w) => string.IsNullOrWhiteSpace(w.Label) ? w.Source : w.Label;

    /// <summary>Finds a channel by source ("CH1", "ch1", "1") or by label ("VIN").</summary>
    public static Waveform? Find(IEnumerable<Waveform> waves, string key)
    {
        key = key.Trim();
        var list = waves.ToList();
        return list.FirstOrDefault(w => w.Source.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(w => w.Label.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(w => w.Source.Equals("CH" + key, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(w => Of(w).Equals(key, StringComparison.OrdinalIgnoreCase));
    }
}
