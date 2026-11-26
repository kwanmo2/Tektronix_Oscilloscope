using System.Text;
using TekCapture;

namespace TekMcp;

/// <summary>TekMcp.exe --selftest &lt;outdir&gt; [synthetic | live | &lt;csv&gt;]
/// Writes report.md and plot.png without an MCP client.</summary>
internal static class SelfTest
{
    public static int Run(string outDir, string? what)
    {
        Directory.CreateDirectory(outDir);
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            WaveSet set;
            if (what == null || what == "synthetic") set = Synthetic();
            else if (what == "live") set = Session.Capture(null, null, true);
            else set = new WaveSet { Waves = CsvImport.Read(what), Origin = "CSV 파일", CsvPath = what };

            var report = Report.Build(set, set.Waves, null, null);
            File.WriteAllText(Path.Combine(outDir, "report.md"), report, new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(outDir, "plot.png"), Plot.Render(set.Waves, null, null));
            var w0 = set.Waves[0];
            double mid = (w0.TStart + w0.TEnd) / 2, span = (w0.TEnd - w0.TStart) / 40;
            File.WriteAllBytes(Path.Combine(outDir, "plot_zoom_overlay.png"), Plot.Render(set.Waves, mid - span, mid + span, overlay: true));
            Console.WriteLine(report);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
    }

    /// <summary>Half-bridge-like test pattern: complementary PWM with dead time, a glitch, a rail with ripple, an enable step.</summary>
    private static WaveSet Synthetic()
    {
        const int n = 200_000;
        const double dt = 1e-9, t0 = -100e-6, f = 100e3, duty = 0.3, dead = 50e-9;
        var rnd = new Random(1);
        float Noise(double a) => (float)((rnd.NextDouble() - 0.5) * 2 * a);
        double Edge(double x, double tau) => 1 / (1 + Math.Exp(-x / tau)); // smooth step

        var hs = new float[n]; var ls = new float[n]; var vout = new float[n]; var en = new float[n];
        double T = 1 / f;
        for (int i = 0; i < n; i++)
        {
            double t = t0 + i * dt;
            double ph = ((t % T) + T) % T;
            // high side on [0, duty*T), low side on [duty*T+dead, T-dead)
            double h = Edge(ph, 3e-9) * (1 - Edge(ph - duty * T, 3e-9));
            double l = Edge(ph - duty * T - dead, 3e-9) * (1 - Edge(ph - (T - dead), 3e-9));
            // ringing after each high-side rising edge
            double ring = ph < 300e-9 ? 0.6 * Math.Exp(-ph / 60e-9) * Math.Sin(2 * Math.PI * 40e6 * ph) : 0;
            hs[i] = (float)(5 * h + ring) + Noise(0.03);
            ls[i] = (float)(5 * l) + Noise(0.03);
            vout[i] = (float)(3.3 + 0.02 * Math.Sin(2 * Math.PI * f * t)) + Noise(0.005);
            en[i] = (float)(3.3 * Edge(t - 20e-6, 20e-9)) + Noise(0.02);
        }
        // glitch on the high side
        int g = (int)((45e-6 - t0) / dt); // in the low phase
        for (int i = g; i < g + 15; i++) hs[i] = 5;

        Waveform W(string src, string label, float[] y, double vdiv, double pos)
        {
            var w = new Waveform { Source = src, Label = label, Y = y, XIncr = dt, XZero = t0, PtOff = 0, ScopeVPerDiv = vdiv, ScopePosDiv = pos, ScopeOffset = 0 };
            w.ComputeStats();
            return w;
        }
        return new WaveSet
        {
            Origin = "합성 테스트 신호",
            Waves =
            [
                W("CH1", "PWM_H", hs, 2, 0),
                W("CH2", "PWM_L", ls, 2, -2),
                W("CH3", "VOUT", vout, 1, -3),
                W("CH4", "", en, 1, 1),
            ],
        };
    }
}
