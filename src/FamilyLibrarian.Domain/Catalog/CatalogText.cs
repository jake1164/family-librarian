using System.Globalization;
using System.Text;

namespace FamilyLibrarian.Domain.Catalog;

public static class CatalogText
{
    public static string NormalizeForMatch(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalized = value.Trim().Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(normalized.Length);
        var pendingSeparator = false;

        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return builder.ToString();
    }
}
