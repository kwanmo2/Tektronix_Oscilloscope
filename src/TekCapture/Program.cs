using System.Diagnostics;

namespace TekCapture;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--loadtest")
        {
            // TekCapture.exe --loadtest <csv> <outdir>
            Directory.CreateDirectory(args[2]);
            using var log = new StreamWriter(Path.Combine(args[2], "loadtest.log"));
            try
            {
                var sw = Stopwatch.StartNew();
                var waves = CsvImport.Read(args[1]);
                log.WriteLine($"Loaded in {sw.ElapsedMilliseconds} ms");
                foreach (var w in waves)
                    log.WriteLine($"{w.Source} label='{w.Label}' {w.YUnit}: {w.Length} pts, dt={w.XIncr:G6}, t0={w.XZero:G6}, min={w.Min:G4}, max={w.Max:G4}, mean={w.Mean:G4}");
                RenderViewer(waves, args[2]);
                log.WriteLine("OK");
                return 0;
            }
            catch (Exception ex) { log.WriteLine("FAIL: " + ex); return 1; }
        }
        if (args.Length >= 2 && args[0] == "--selftest")
            return SelfTest(args[1], args[2..]);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>Headless check: TekCapture.exe --selftest &lt;outdir&gt;  (writes selftest.log, PNG, CSV)</summary>
    /// <summary>Temporarily switch record length, capture, render, restore.</summary>
    private static int LongRecordTest(TekScope scope, int len, string outDir, Action<string> L)
    {
        var before = scope.GetHorizontal();
        try
        {
            scope.SetRecordLength(len);
            Thread.Sleep(1500); // let a new acquisition complete
            var h = scope.GetHorizontal();
            L($"Set: {h.RecordLength} pts, {h.TimePerDiv:G4} s/div, {h.SampleRate:G4} S/s, duration {h.Duration:G4} s");
            var waves = new List<Waveform>();
            foreach (var s in scope.DisplayedSources())
            {
                var sw = Stopwatch.StartNew();
                waves.Add(scope.ReadWaveform(s));
                L($"{s}: {waves[^1].Length} pts in {sw.ElapsedMilliseconds} ms");
            }
            var sw2 = Stopwatch.StartNew();
            CsvExport.Write(Path.Combine(outDir, "wave_long.csv"), waves);
            L($"CSV {new FileInfo(Path.Combine(outDir, "wave_long.csv")).Length / 1e6:F1} MB in {sw2.ElapsedMilliseconds} ms");
            RenderViewer(waves, outDir);
            L("OK");
            return 0;
        }
        finally
        {
            scope.SetRecordLength(before.RecordLength, before.TimePerDiv);
            L($"Restored record length {before.RecordLength}");
        }
    }

    private static int SelfTest(string outDir, params string[] args)
    {
        Directory.CreateDirectory(outDir);
        using var log = new StreamWriter(Path.Combine(outDir, "selftest.log"));
        void L(string s) { log.WriteLine(s); log.Flush(); }
        try
        {
            var rsrcs = VisaSession.FindResources();
            L("Resources: " + string.Join(", ", rsrcs));
            var rsrc = rsrcs.First(r => r.Contains("0x0699", StringComparison.OrdinalIgnoreCase));
            using var scope = new TekScope(rsrc);
            L("IDN: " + scope.Idn);
            if (args.Length >= 1 && int.TryParse(args[0], out int testLen))
                return LongRecordTest(scope, testLen, outDir, L);

            var sw = Stopwatch.StartNew();
            var png = scope.CaptureScreen(false);
            File.WriteAllBytes(Path.Combine(outDir, "screen.png"), png);
            L($"Screen: {png.Length} bytes, {sw.ElapsedMilliseconds} ms");

            var srcs = scope.DisplayedSources();
            L("Displayed: " + string.Join(", ", srcs) + $"  RecordLength={scope.RecordLength}");
            var waves = new List<Waveform>();
            foreach (var s in srcs)
            {
                sw.Restart();
                var w = scope.ReadWaveform(s);
                waves.Add(w);
                L($"{s}: {w.Length} pts, dt={w.XIncr:G4}, t0={w.XZero:G4}, min={w.Min:G4}, max={w.Max:G4}, mean={w.Mean:G4} {w.YUnit}, " +
                  $"scope {w.ScopeVPerDiv:G4} {w.YUnit}/div pos={w.ScopePosDiv:G4} off={w.ScopeOffset:G4} label='{w.Label}', {sw.ElapsedMilliseconds} ms");
            }
            var h = scope.GetHorizontal();
            L($"Horizontal: {h.RecordLength} pts, {h.TimePerDiv:G4} s/div, {h.SampleRate:G4} S/s, duration {h.Duration:G4} s");
            if (waves.Count > 0)
            {
                waves[0].Label = "TestLabel";
                CsvExport.Write(Path.Combine(outDir, "wave.csv"), waves);
                RenderViewer(waves, outDir);
                L("Viewer renders written");
            }
            L("OK");
            return 0;
        }
        catch (Exception ex)
        {
            L("FAIL: " + ex);
            return 1;
        }
    }

    private static void RenderViewer(List<Waveform> waves, string outDir)
    {
        ApplicationConfiguration.Initialize();
        using var form = new Form { Width = 1400, Height = 820, StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000), ShowInTaskbar = false };
        var panel = new WaveformPanel { Dock = DockStyle.Fill };
        form.Controls.Add(panel);
        form.Show();
        panel.SetWaveforms(waves);

        void Snap(string name)
        {
            Application.DoEvents();
            using var bmp = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            panel.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.ClientSize));
            bmp.Save(Path.Combine(outDir, name));
        }

        Snap("view_scope.png");
        panel.View.StackChannels();
        Snap("view_stacked.png");
        panel.View.SetTimePerDiv(panel.View.MinDx * 2, panel.View.TCenter);
        Snap("view_zoomed.png");
    }
}
