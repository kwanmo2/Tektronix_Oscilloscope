using System.Runtime.InteropServices;
using System.Text;

namespace TekCapture;

/// <summary>Minimal P/Invoke binding to the 64-bit VISA router (visa64.dll).
/// Works with any vendor VISA (Keysight, NI, TekVISA) that is installed as the system VISA.</summary>
internal static class VisaNative
{
    private const string Dll = "visa64.dll";

    public const int VI_SUCCESS = 0;
    public const int VI_SUCCESS_TERM_CHAR = 0x3FFF0005;
    public const int VI_SUCCESS_MAX_CNT = 0x3FFF0006;
    public const int VI_ERROR_RSRC_NFOUND = unchecked((int)0xBFFF0011);
    public const uint VI_ATTR_TMO_VALUE = 0x3FFF001A;

    [DllImport(Dll)] public static extern int viOpenDefaultRM(out uint rm);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int viFindRsrc(uint rm, string expr, out uint list, out uint count, StringBuilder desc);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int viFindNext(uint list, StringBuilder desc);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int viOpen(uint rm, string name, uint mode, uint timeout, out uint vi);
    [DllImport(Dll)] public static extern int viClose(uint vi);
    [DllImport(Dll)] public static extern int viClear(uint vi);
    [DllImport(Dll)] public static extern int viWrite(uint vi, byte[] buf, uint count, out uint retCount);
    [DllImport(Dll)] public static extern int viRead(uint vi, byte[] buf, uint count, out uint retCount);
    [DllImport(Dll)] public static extern int viSetAttribute(uint vi, uint attr, nuint value);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int viStatusDesc(uint vi, int status, StringBuilder desc);
}

public sealed class VisaException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>One open VISA instrument session.</summary>
public sealed class VisaSession : IDisposable
{
    private static uint s_rm;
    private static readonly object s_rmLock = new();

    private uint _vi;
    private int _timeoutMs;
    private readonly byte[] _chunk = new byte[1 << 20];

    private static uint ResourceManager
    {
        get
        {
            lock (s_rmLock)
            {
                if (s_rm == 0)
                    Check(VisaNative.viOpenDefaultRM(out s_rm), 0, "viOpenDefaultRM");
                return s_rm;
            }
        }
    }

    public static List<string> FindResources(string expr = "?*INSTR")
    {
        var result = new List<string>();
        var sb = new StringBuilder(512);
        int st = VisaNative.viFindRsrc(ResourceManager, expr, out uint list, out uint count, sb);
        if (st == VisaNative.VI_ERROR_RSRC_NFOUND) return result;
        Check(st, ResourceManager, "viFindRsrc");
        result.Add(sb.ToString());
        for (int i = 1; i < count; i++)
        {
            if (VisaNative.viFindNext(list, sb) < 0) break;
            result.Add(sb.ToString());
        }
        VisaNative.viClose(list);
        return result;
    }

    public VisaSession(string resource, int timeoutMs = 5000)
    {
        Check(VisaNative.viOpen(ResourceManager, resource, 0, 0, out _vi), ResourceManager, $"viOpen({resource})");
        TimeoutMs = timeoutMs;
    }

    public int TimeoutMs
    {
        get => _timeoutMs;
        set
        {
            Check(VisaNative.viSetAttribute(_vi, VisaNative.VI_ATTR_TMO_VALUE, (nuint)value), _vi, "set timeout");
            _timeoutMs = value;
        }
    }

    public void Write(string command)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\n");
        Check(VisaNative.viWrite(_vi, bytes, (uint)bytes.Length, out _), _vi, $"write '{command}'");
    }

    /// <summary>Reads until END (EOI) is received.</summary>
    public byte[] ReadRaw()
    {
        using var ms = new MemoryStream();
        while (true)
        {
            int st = VisaNative.viRead(_vi, _chunk, (uint)_chunk.Length, out uint n);
            Check(st, _vi, "read");
            ms.Write(_chunk, 0, (int)n);
            if (st != VisaNative.VI_SUCCESS_MAX_CNT) break;
        }
        return ms.ToArray();
    }

    public string Query(string command)
    {
        Write(command);
        return Encoding.ASCII.GetString(ReadRaw()).TrimEnd('\r', '\n', ' ');
    }

    public void Clear() => VisaNative.viClear(_vi);

    public void Dispose()
    {
        if (_vi != 0)
        {
            VisaNative.viClose(_vi);
            _vi = 0;
        }
    }

    private static void Check(int status, uint vi, string what)
    {
        if (status >= 0) return;
        var sb = new StringBuilder(512);
        VisaNative.viStatusDesc(vi, status, sb);
        throw new VisaException($"{what}: {sb} (0x{status:X8})", status);
    }
}
