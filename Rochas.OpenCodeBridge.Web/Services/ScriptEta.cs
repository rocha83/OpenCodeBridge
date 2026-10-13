using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Web.Services;

// ETA estimada pelo modelo no cabeçalho do script (`# ETA: ~12 min`).
// Ausente/imprevisível = vazio (null), nunca chute.
public static partial class ScriptEta
{
    // Só o começo do arquivo interessa (cabeçalho).
    private const int HeadChars = 4096;

    [GeneratedRegex(@"^\s*#\s*ETA\s*:\s*~?\s*([\d.,]+)\s*min", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex EtaRegex();

    public static double? ParseMinutes(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        string head = content.Length > HeadChars ? content[..HeadChars] : content;
        var m = EtaRegex().Match(head);
        if (!m.Success) return null;
        if (double.TryParse(m.Groups[1].Value.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0)
            return v;
        return null;
    }
}
