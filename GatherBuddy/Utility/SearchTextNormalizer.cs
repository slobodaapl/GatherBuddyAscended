using System.Globalization;
using System.Text;

namespace GatherBuddy.Utility;

internal static class SearchTextNormalizer
{
    internal static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var result = new StringBuilder(value.Length);
        foreach (var rune in value.Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark
                         or UnicodeCategory.SpacingCombiningMark
                         or UnicodeCategory.EnclosingMark)
                continue;
            if (Rune.IsLetterOrDigit(rune))
                result.Append(Rune.ToLowerInvariant(rune));
        }

        return result.ToString();
    }
}
