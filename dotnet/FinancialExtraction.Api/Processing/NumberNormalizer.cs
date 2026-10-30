// Turns an amount as printed in the document into a number.
// It handles brackets (negative amounts), scales such as thousands or millions,
// and currency symbols, so the arithmetic checks can run on real values.

using System.Globalization;
using System.Text.RegularExpressions;

namespace FinancialExtraction.Api.Processing;

public static class NumberNormalizer
{
    private static readonly Dictionary<char, string> CurrencySymbols = new()
    {
        ['€'] = "EUR", ['$'] = "USD", ['£'] = "GBP",
    };

    private static readonly HashSet<string> NilTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "-", "–", "—", "nil", "none", "n/a", "na",
    };

    public static string? DetectCurrency(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var (sym, code) in CurrencySymbols)
            if (text.Contains(sym)) return code;
        var upper = text.ToUpperInvariant();
        if (Regex.IsMatch(upper, @"\b(EUR|EURO|EUROS)\b")) return "EUR";
        if (Regex.IsMatch(upper, @"\b(USD|DOLLAR|DOLLARS)\b")) return "USD";
        if (Regex.IsMatch(upper, @"\b(GBP|STG|STERLING|POUND|POUNDS)\b")) return "GBP";
        return null;
    }

    public static long DetectScale(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption)) return 1;
        var c = caption.Trim();

        // Billions: the word, a "000 000" grid, "€'bn", or a caption that is just "bn".
        if (Regex.IsMatch(c, @"\bbillion", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(c, @"000['’]?\s*000") ||
            Regex.IsMatch(c, @"[€£$]\s*['’]?\s*bn\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(c, @"^['’]?\s*bn['’.]?$", RegexOptions.IgnoreCase)) return 1_000_000_000;

        // Millions: the word, "€'m" / "€m", or a caption that is just "m" / "mn" / "mm".
        // The model often returns the scale field stripped of its currency symbol, so a
        // bare "m" has to be read as millions on its own.
        if (Regex.IsMatch(c, @"\bmillion", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(c, @"[€£$]\s*['’]?\s*m\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(c, @"^['’]?\s*m[mn]?['’.]?$", RegexOptions.IgnoreCase)) return 1_000_000;

        // Thousands: "€'000" / "000", "in thousands", or a caption that is just "k".
        if (Regex.IsMatch(c, @"['’]?\s*000s?\b") ||
            Regex.IsMatch(c, @"\bin\s+thousands?\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(c, @"^['’]?\s*k['’.]?$", RegexOptions.IgnoreCase)) return 1_000;

        return 1;
    }

    public record ParsedNumber(double? Value, string Raw, string? Currency, bool IsNegative, long Scale, string Note);

    public static ParsedNumber Parse(string? raw, long tableScale = 1)
    {
        if (raw is null) return new ParsedNumber(null, "", null, false, 1, "empty");
        var original = raw.Trim();
        var currency = DetectCurrency(original);

        if (NilTokens.Contains(original))
            return new ParsedNumber(0, original, currency, false, 1, "");

        var cleaned = original;
        bool negative = false;

        if (Regex.IsMatch(cleaned, @"^\(.*\)$")) { negative = true; cleaned = cleaned[1..^1]; }
        if (cleaned.TrimEnd().EndsWith("-")) { negative = true; cleaned = cleaned.TrimEnd(' ', '-'); }
        if (cleaned.TrimStart().StartsWith("-")) negative = true;

        long suffixFactor = 1;
        if (Regex.IsMatch(cleaned, @"(?<=\d)\s*bn\b|\bbillion\b", RegexOptions.IgnoreCase)) suffixFactor = 1_000_000_000;
        else if (Regex.IsMatch(cleaned, @"(?<=\d)\s*m\b|\bmillion\b", RegexOptions.IgnoreCase)) suffixFactor = 1_000_000;
        else if (Regex.IsMatch(cleaned, @"(?<=\d)\s*k\b|\bthousand\b", RegexOptions.IgnoreCase)) suffixFactor = 1_000;

        var numeric = Regex.Replace(cleaned, @"[^\d.\-]", "");
        if (numeric is "" or "-" or "." or "-.")
            return new ParsedNumber(null, original, currency, negative, 1, "no numeric content");

        if (!double.TryParse(numeric, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            return new ParsedNumber(null, original, currency, negative, 1, $"unparseable: {numeric}");

        value = Math.Abs(value);
        long effectiveScale = suffixFactor != 1 ? suffixFactor : tableScale;
        value *= effectiveScale;
        if (negative) value = -value;

        return new ParsedNumber(value, original, currency, negative, effectiveScale, "");
    }
}
