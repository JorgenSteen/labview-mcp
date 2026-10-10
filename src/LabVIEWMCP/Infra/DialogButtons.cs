// =============================================================================
// DialogButtons.cs - pictures of LabVIEW dialog buttons cut from measured screenshots,
// and the search that finds one in a fresh screenshot before anything is clicked.
// =============================================================================
namespace LabVIEWMcp.Infra;

/// <summary>
/// LabVIEW draws its dialog buttons itself, so there is no button handle to press and no text
/// to find. A button is located by its PICTURE: a crop of a screenshot taken of the real dialog,
/// shipped here as PNG. A click goes only where that picture is found again, nearly pixel for
/// pixel; a different layout, DPI or theme finds nothing and nothing is pressed.
/// </summary>
internal static class DialogButtons
{
    /// <summary>(dialog kind, action) -> base64 PNG of the button, from a measured screenshot.</summary>
    private static readonly Dictionary<(string Kind, string Action), string> Pictures = new()
    {
        // "Don't Save" of LabVIEW 2026 Q3's "Save changes? (Exit)" for ONE VI (buttons Save / Don't
        // Save / Cancel), the text on the button face, 58x15 at (34,174) of a 412x249 PrintWindow
        // picture, 2026-10-10 14:12 (96 dpi, Windows 10). Found again at 0.0 mean difference in three
        // pictures of two prompts; the next-best place scored 21.2.
        [(DialogAnswer.ExitSaveScratchOnly, DialogAnswer.DontSave)] =
            "iVBORw0KGgoAAAANSUhEUgAAADoAAAAPCAIAAAA6bkthAAAA7UlEQVR42u2V2w3DIAxF2YltvIxX8SqM0k1aKH4BhSKhSpXwTwKB64N9k4THX0W4uD/CJQgaEdO+GsF8m2YAOoEr2YrwtqbgWimOhFEUEyKdxPXqR3BHc+dwLW++bbpYFiOMnGNM5c6b5xtg7w5fIcHR7PANbtY03O81xi3m+aqSNXPXNV7/upp6cHaWogVuGXZnblUGcNPGF+aqqOWVKnAN2tKWWHv3OK5IqjKXhu/c2GSffRlMW6wZxAGbuIQ8w5Zy7uT1mRQgCqJz/Kfvrk+mHRkiDuCqVveq+VeQdTNftIdxG60f7k/44nI8AfUsqA2l89evAAAAAElFTkSuQmCC",
    };

    public static WindowImage? For(string kind, string action) =>
        Pictures.TryGetValue((kind, action), out var b64) ? WindowShot.DecodePng(Convert.FromBase64String(b64)) : null;

    /// <summary>
    /// The top-left of the ONE place <paramref name="needle"/> appears in <paramref name="hay"/>,
    /// within a mean difference of <paramref name="maxMeanDiff"/> per channel. Null when it is
    /// nowhere - or in two places that do not overlap, which would make the click a guess.
    /// </summary>
    public static (int X, int Y)? Find(WindowImage hay, WindowImage needle, double maxMeanDiff = 6)
    {
        if (needle.Width > hay.Width || needle.Height > hay.Height) return null;
        var limit = (long)(maxMeanDiff * needle.Width * needle.Height * 3);
        (int X, int Y, long Diff)? best = null;
        var places = new List<(int X, int Y)>();
        for (var y = 0; y <= hay.Height - needle.Height; y++)
            for (var x = 0; x <= hay.Width - needle.Width; x++)
            {
                var diff = Diff(hay, needle, x, y, limit);
                if (diff > limit) continue;
                places.Add((x, y));
                if (best is null || diff < best.Value.Diff) best = (x, y, diff);
            }
        if (best is not { } b) return null;
        var separate = places.Any(p => Math.Abs(p.X - b.X) >= needle.Width || Math.Abs(p.Y - b.Y) >= needle.Height);
        return separate ? null : (b.X, b.Y);
    }

    private static long Diff(WindowImage hay, WindowImage needle, int ox, int oy, long limit)
    {
        long sum = 0;
        for (var y = 0; y < needle.Height; y++)
        {
            var h = ((oy + y) * hay.Width + ox) * 3;
            var n = y * needle.Width * 3;
            for (var i = 0; i < needle.Width * 3; i++)
                sum += Math.Abs(hay.Rgb[h + i] - needle.Rgb[n + i]);
            if (sum > limit) return sum;
        }
        return sum;
    }
}
