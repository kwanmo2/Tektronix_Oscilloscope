using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TekCapture;

/// <summary>Loads CSV files written by <see cref="CsvExport"/> (and simple "time, value, value…" CSVs).
/// Layouts: shared time  → "Time (s),CH1 VIN (V),CH2 (V)…"
///          per-channel  → "CH1 Time (s),CH1 (V),CH2 Time (s),CH2 (V)…"</summary>
public static partial class CsvImport
{
    [GeneratedRegex(@"^(?<name>.*?)\s*\((?<unit>[^()]*)\)\s*$")]
    private static partial Regex NameUnitRx();

    [GeneratedRegex(@"^(CH\d+|MATH\d*|REF\d+|D\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SourceRx();

    private sealed class Column(string name, string unit)
    {
        public string Name = name;
        public string Unit = unit;
        public List<double> T = [];
        public List<float> Y = [];
    }

    public static List<Waveform> Read(string path, IProgress<string>? progress = null)
    {
        using var sr = new StreamReader(path, Encoding.UTF8, true, 1 << 20);

        // header = first line whose following line starts with a number
        string? header = null, line;
        while ((line = sr.ReadLine()) != null)
        {
            if (line.Length > 0 && IsNumber(FirstCell(line)))
            {
                if (header == null) throw new InvalidDataException("헤더 줄이 없습니다.");
                break;
            }
            header = line;
        }
        if (header == null || line == null) throw new InvalidDataException("데이터가 없습니다.");

        var heads = SplitHeader(header);
        if (heads.Count < 2) throw new InvalidDataException("열이 2개 이상이어야 합니다 (시간, 값).");
        bool pairs = heads.Count > 2 && heads.Count % 2 == 0 &&
                     heads.Where((_, i) => i % 2 == 0).All(h => h.Contains("time", StringComparison.OrdinalIgnoreCase));

        var (_, tUnit) = ParseHead(heads[0], "s");
        var cols = new List<Column>();
        if (pairs)
            for (int i = 1; i < heads.Count; i += 2) { var (n, u) = ParseHead(heads[i], "V"); cols.Add(new Column(n, u)); }
        else
            for (int i = 1; i < heads.Count; i++) { var (n, u) = ParseHead(heads[i], "V"); cols.Add(new Column(n, u)); }
        var shared = new List<double>();

        var inv = CultureInfo.InvariantCulture;
        long row = 0;
        do
        {
            if (line.Length == 0) continue;
            var cells = line.Split(',');
            if (pairs)
            {
                for (int k = 0; k < cols.Count && 2 * k + 1 < cells.Length; k++)
                {
                    if (cells[2 * k].Length == 0 || cells[2 * k + 1].Length == 0) continue;
                    if (double.TryParse(cells[2 * k], NumberStyles.Float, inv, out var t) &&
                        float.TryParse(cells[2 * k + 1], NumberStyles.Float, inv, out var y))
                    { cols[k].T.Add(t); cols[k].Y.Add(y); }
                }
            }
            else
            {
                if (!double.TryParse(cells[0], NumberStyles.Float, inv, out var t)) continue;
                shared.Add(t);
                for (int k = 0; k < cols.Count; k++)
                    cols[k].Y.Add(k + 1 < cells.Length && float.TryParse(cells[k + 1], NumberStyles.Float, inv, out var y) ? y : float.NaN);
            }
            if (++row % 1_000_000 == 0) progress?.Report($"  {row:N0} 행 읽음…");
        } while ((line = sr.ReadLine()) != null);

        var result = new List<Waveform>();
        for (int k = 0; k < cols.Count; k++)
        {
            var c = cols[k];
            var t = pairs ? c.T : shared;
            int n = Math.Min(t.Count, c.Y.Count);
            if (n < 2) continue;
            double dx = (t[n - 1] - t[0]) / (n - 1);
            if (!(dx > 0)) throw new InvalidDataException($"{c.Name}: 시간 값이 증가하지 않습니다.");

            var y = c.Y.Count == n ? c.Y.ToArray() : c.Y.GetRange(0, n).ToArray();
            // fill gaps (NaN) with the previous value so stats/plot stay sane
            float last = 0;
            for (int i = 0; i < y.Length; i++) { if (float.IsNaN(y[i])) y[i] = last; else last = y[i]; }

            var (source, label) = SplitName(c.Name, k);
            var w = new Waveform
            {
                Source = source, Label = label, Y = y, XIncr = dx, XZero = t[0], PtOff = 0,
                XUnit = tUnit, YUnit = c.Unit,
            };
            w.ComputeStats();
            result.Add(w);
        }
        if (result.Count == 0) throw new InvalidDataException("읽을 수 있는 파형이 없습니다.");
        return result;
    }

    private static (string Name, string Unit) ParseHead(string h, string defUnit)
    {
        var m = NameUnitRx().Match(h.Trim());
        return m.Success ? (m.Groups["name"].Value.Trim(), m.Groups["unit"].Value.Trim()) : (h.Trim(), defUnit);
    }

    /// <summary>"CH1 VIN" → (CH1, VIN); "Voltage" → (Voltage, "")</summary>
    private static (string Source, string Label) SplitName(string name, int index)
    {
        if (string.IsNullOrWhiteSpace(name)) return ($"COL{index + 1}", "");
        int sp = name.IndexOf(' ');
        string first = sp < 0 ? name : name[..sp];
        if (SourceRx().IsMatch(first))
            return (first.ToUpperInvariant(), sp < 0 ? "" : name[(sp + 1)..].Trim());
        return (name, "");
    }

    private static string FirstCell(string line)
    {
        int i = line.IndexOf(',');
        return (i < 0 ? line : line[..i]).Trim();
    }

    private static bool IsNumber(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static List<string> SplitHeader(string line)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (q)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') q = false;
                else sb.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == ',') { list.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        list.Add(sb.ToString());
        return list;
    }
}
