namespace FamilyLibrarian.Infrastructure.Metadata;

internal static class IsbnNormalizer
{
    public static bool TryNormalizeQuery(string value, out string normalized)
    {
        var candidate = RemoveSeparators(value);
        if (IsValidIsbn13(candidate) || IsValidIsbn10(candidate))
        {
            normalized = candidate;
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    public static bool TryNormalizeToIsbn13(string value, out string isbn13)
    {
        var candidate = RemoveSeparators(value);
        if (IsValidIsbn13(candidate))
        {
            isbn13 = candidate;
            return true;
        }

        if (!IsValidIsbn10(candidate))
        {
            isbn13 = string.Empty;
            return false;
        }

        var firstTwelveDigits = $"978{candidate[..9]}";
        isbn13 = $"{firstTwelveDigits}{CalculateIsbn13CheckDigit(firstTwelveDigits)}";
        return true;
    }

    private static string RemoveSeparators(string value) =>
        new(value
            .Where(character => character is not (' ' or '-'))
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static bool IsValidIsbn10(string candidate)
    {
        if (candidate.Length != 10)
        {
            return false;
        }

        var sum = 0;
        for (var index = 0; index < candidate.Length; index++)
        {
            var character = candidate[index];
            var value = character == 'X' && index == 9
                ? 10
                : character is >= '0' and <= '9'
                    ? character - '0'
                    : -1;

            if (value < 0)
            {
                return false;
            }

            sum += (10 - index) * value;
        }

        return sum % 11 == 0;
    }

    private static bool IsValidIsbn13(string candidate)
    {
        if (candidate.Length != 13 || candidate.Any(character => character is < '0' or > '9'))
        {
            return false;
        }

        var expectedCheckDigit = CalculateIsbn13CheckDigit(candidate[..12]);
        return candidate[12] - '0' == expectedCheckDigit;
    }

    private static int CalculateIsbn13CheckDigit(string firstTwelveDigits)
    {
        var sum = 0;
        for (var index = 0; index < firstTwelveDigits.Length; index++)
        {
            var value = firstTwelveDigits[index] - '0';
            sum += value * (index % 2 == 0 ? 1 : 3);
        }

        return (10 - (sum % 10)) % 10;
    }
}
