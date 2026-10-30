// Decides whether an amount is negative or positive.
// It looks at the label of the field (for example when it contains the word "Loss"),
// and handles the special case "Loss/(profit)", where brackets do not mean negative.

using System.Text.RegularExpressions;

namespace FinancialExtraction.Api.Processing;

public static class SignRules
{

    private static readonly Regex LossFirstNeutral = new(
        @"loss\s*/\s*\(?\s*profit|inflow\s*/\s*\(?\s*outflow|increase\s*/\s*\(?\s*decrease",
        RegexOptions.IgnoreCase);

    private static readonly Regex HasLoss = new(@"\bloss\b", RegexOptions.IgnoreCase);
    private static readonly Regex HasProfit = new(@"\bprofit\b", RegexOptions.IgnoreCase);

    public static double? Resolve(string label, string? raw, double? parsedValue)
    {
        if (parsedValue is null) return null;
        label ??= "";
        raw ??= "";

        bool bracketed = Regex.IsMatch(raw.Trim(), @"^\(.*\)$");

        if (bracketed && LossFirstNeutral.IsMatch(label))
            return Math.Abs(parsedValue.Value);

        if (HasLoss.IsMatch(label) && !HasProfit.IsMatch(label))
            return -Math.Abs(parsedValue.Value);

        return parsedValue;
    }
}
