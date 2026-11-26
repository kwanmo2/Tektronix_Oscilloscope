using System.ComponentModel;
using System.Globalization;
using System.Windows.Forms;

namespace TekCapture;

public enum UiLang { Ko, En }

/// <summary>UI language (Korean / English). Strings are written inline as <c>Loc.T("한국어", "English")</c>.
/// Static texts bound with <see cref="Bind"/> (or the <c>L(...)</c> helpers) are re-applied when the language
/// changes; dynamic texts are rebuilt by the <see cref="Changed"/> handlers.</summary>
public static class Loc
{
    public static UiLang Lang { get; private set; } = UiLang.Ko;
    public static bool IsEn => Lang == UiLang.En;
    public static string Code => IsEn ? "en" : "ko";

    /// <summary>Raised after the language changed and all bound texts were updated.</summary>
    public static event EventHandler? Changed;

    public static string T(string ko, string en) => Lang == UiLang.En ? en : ko;

    public static UiLang Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "en" or "eng" or "english" => UiLang.En,
        "ko" or "kor" or "korean" => UiLang.Ko,
        _ => Default,
    };

    /// <summary>Korean on a Korean Windows, English elsewhere.</summary>
    public static UiLang Default => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko" ? UiLang.Ko : UiLang.En;

    public static void Set(UiLang lang)
    {
        if (lang == Lang) return;
        Lang = lang;
        Binds.RemoveAll(b => IsDead(b.Owner));
        foreach (var b in Binds.ToList()) b.Apply();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static readonly List<(Component Owner, Action Apply)> Binds = [];

    /// <summary>Runs <paramref name="apply"/> now and again on every language change while the owner lives.</summary>
    public static void Bind(Component owner, Action apply)
    {
        apply();
        Binds.Add((owner, apply));
    }

    private static bool IsDead(Component c) => c switch
    {
        Control k => k.IsDisposed,
        ToolStripItem t => t.IsDisposed,
        _ => false,
    };

    /// <summary>Binds the control's Text.</summary>
    public static TC L<TC>(this TC c, string ko, string en) where TC : Control
    {
        Bind(c, () => c.Text = T(ko, en));
        return c;
    }

    /// <summary>Binds the menu item's Text.</summary>
    public static ToolStripMenuItem L(this ToolStripMenuItem i, string ko, string en)
    {
        Bind(i, () => i.Text = T(ko, en));
        return i;
    }

    /// <summary>Binds a tooltip.</summary>
    public static void Tip(this ToolTip tip, Control c, string ko, string en) => Bind(c, () => tip.SetToolTip(c, T(ko, en)));
}
