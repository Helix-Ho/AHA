using System.Text.RegularExpressions;

namespace Aha.Topo;

/// <summary>The 28 cadastral categories and their registered-map symbols.</summary>
public static class ParcelLandCategory
{
    public const int Road = 14;
    private const string Symbols = "전답과목임광염대장학차주창도철제천구유양수공체원종사묘잡";
    private static readonly Regex Label = new(
        @"\A\s*(?:산\s*)?[0-9]+(?:\s*-\s*[0-9]+)?\s*([전답과목임광염대장학차주창도철제천구유양수공체원종사묘잡])\s*\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static int? ParseLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var match = Label.Match(label);
        return match.Success ? Symbols.IndexOf(match.Groups[1].Value[0]) + 1 : null;
    }

    public static bool IsKnown(int? code) => code is >= 1 and <= 28;
}
