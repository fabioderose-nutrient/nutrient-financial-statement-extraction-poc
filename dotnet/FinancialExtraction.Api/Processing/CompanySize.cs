// Classifies the company as micro, small, medium or large.
// It first uses the accounting standard (FRS or IFRS), which implies the size, and
// otherwise the statutory thresholds for turnover, balance sheet total and employees.

using System.Text.RegularExpressions;

namespace FinancialExtraction.Api.Processing;

public enum CompanySize { Micro, Small, Medium, Large, Unknown }

public enum Framework { Frs105, Frs102Section1A, Frs102, Frs101, Frs100, Ifrs, Unknown }

public static class CompanySizeClassifier
{
    private record Limits(double Turnover, double BalanceSheet, int Employees);

    private static readonly Dictionary<CompanySize, Limits> Thresholds = new()
    {
        [CompanySize.Micro]  = new(900_000, 450_000, 10),
        [CompanySize.Small]  = new(15_000_000, 7_500_000, 50),
        [CompanySize.Medium] = new(50_000_000, 25_000_000, 250),
    };

    private static readonly (Framework fw, Regex rx)[] FrameworkPatterns =
    {
        (Framework.Frs102Section1A, new Regex(@"section\s*1a", RegexOptions.IgnoreCase)),
        (Framework.Frs105, new Regex(@"frs\s*105", RegexOptions.IgnoreCase)),
        (Framework.Frs101, new Regex(@"frs\s*101", RegexOptions.IgnoreCase)),
        (Framework.Frs100, new Regex(@"frs\s*100", RegexOptions.IgnoreCase)),
        (Framework.Frs102, new Regex(@"frs\s*102", RegexOptions.IgnoreCase)),
        (Framework.Ifrs, new Regex(@"\bifrs\b|international financial reporting standards", RegexOptions.IgnoreCase)),
    };

    private static readonly Dictionary<Framework, CompanySize> DecisiveFramework = new()
    {
        [Framework.Frs105] = CompanySize.Micro,
        [Framework.Frs102Section1A] = CompanySize.Small,
        [Framework.Frs101] = CompanySize.Medium,
        [Framework.Ifrs] = CompanySize.Large,
    };

    public static object ThresholdTable() => new[]
    {
        new { size = "micro",  turnover = "≤ €900,000",    balance_sheet = "≤ €450,000",    employees = "≤ 10",  framework = "FRS 105" },
        new { size = "small",  turnover = "≤ €15,000,000", balance_sheet = "≤ €7,500,000",  employees = "≤ 50",  framework = "FRS 102 Section 1A" },
        new { size = "medium", turnover = "≤ €50,000,000", balance_sheet = "≤ €25,000,000", employees = "≤ 250", framework = "FRS 101" },
        new { size = "large",  turnover = "> €50,000,000", balance_sheet = "> €25,000,000", employees = "> 250", framework = "IFRS" },
    };

    public static Framework DetectFramework(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Framework.Unknown;
        foreach (var (fw, rx) in FrameworkPatterns)
            if (rx.IsMatch(text)) return fw;
        return Framework.Unknown;
    }

    public static string FrameworkLabel(Framework f) => f switch
    {
        Framework.Frs105 => "FRS 105",
        Framework.Frs102Section1A => "FRS 102 Section 1A",
        Framework.Frs102 => "FRS 102",
        Framework.Frs101 => "FRS 101",
        Framework.Frs100 => "FRS 100",
        Framework.Ifrs => "IFRS",
        _ => "unknown",
    };

    public record SizeResult(CompanySize Size, Framework Framework, string Method, List<string> Reasons);

    private static int ExceedsCount(double? turnover, double? bs, int? emp, Limits l)
    {
        int c = 0;
        if (turnover is { } t && Math.Abs(t) > l.Turnover) c++;
        if (bs is { } b && Math.Abs(b) > l.BalanceSheet) c++;
        if (emp is { } e && e > l.Employees) c++;
        return c;
    }

    private static CompanySize? FromThresholds(double? turnover, double? bs, int? emp)
    {
        int present = (turnover is null ? 0 : 1) + (bs is null ? 0 : 1) + (emp is null ? 0 : 1);
        if (present == 0) return null;

        int needed = Math.Min(2, present);
        foreach (var size in new[] { CompanySize.Micro, CompanySize.Small, CompanySize.Medium })
            if (ExceedsCount(turnover, bs, emp, Thresholds[size]) < needed) return size;
        return CompanySize.Large;
    }

    public static SizeResult Classify(double? turnover = null, double? balanceSheetTotal = null,
                                      int? employees = null, string documentText = "")
    {
        var framework = DetectFramework(documentText);
        var thSize = FromThresholds(turnover, balanceSheetTotal, employees);

        if (DecisiveFramework.TryGetValue(framework, out var fwSize))
        {
            var reasons = new List<string> { $"{FrameworkLabel(framework)} → {fwSize.ToString().ToLowerInvariant()} (FRS/IFRS size mapping)." };
            if (thSize is { } t && t != fwSize)
                reasons.Add($"Numeric criteria suggest {t.ToString().ToLowerInvariant()} - flag for manual review.");
            return new SizeResult(fwSize, framework, "frs_framework", reasons);
        }

        if (thSize is { } s)
        {
            var reasons = new List<string> { $"Numeric thresholds: does not exceed 2+ of the {s.ToString().ToLowerInvariant()} limits." };
            if (framework != Framework.Unknown)
                reasons.Add($"{FrameworkLabel(framework)} present but not size-decisive on its own.");
            return new SizeResult(s, framework, "thresholds", reasons);
        }

        if (framework == Framework.Frs102)
            return new SizeResult(CompanySize.Small, framework, "frs_framework_default",
                new() { "Full FRS 102 with no numeric criteria - defaulting to small; verify." });

        return new SizeResult(CompanySize.Unknown, framework, "default",
            new() { "No decisive FRS/IFRS reference and no numeric criteria - flag for manual review." });
    }
}
