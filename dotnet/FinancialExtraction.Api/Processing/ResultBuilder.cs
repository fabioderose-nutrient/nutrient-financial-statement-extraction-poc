// Builds the final result shown by the frontend from the raw extracted data:
// the fields for each year, the size classification, the checks, the notes and
// the cost.

using FinancialExtraction.Api.Templates;

namespace FinancialExtraction.Api.Processing;

public class ResultBuilder
{
    private readonly FieldSpecFile _spec;
    public ResultBuilder(FieldSpecFile spec) => _spec = spec;

    public record CommentaryBlock(string? Text, string? Sentiment, string? Reason);

    public object Build(
        Dictionary<string, string?> rawCurrent,
        Dictionary<string, string?> rawPrior,
        string? scaleCaption,
        string? frameworkText,
        string? currencyText,
        string docType,
        string docTypeLabel,
        string confidence,
        Dictionary<string, CommentaryBlock> commentary,
        object cost,
        int? currentYear,
        int? priorYear,
        List<string> warnings,
        object? notes = null,
        System.Text.Json.Nodes.JsonArray? tableRows = null)
    {
        long scale = NumberNormalizer.DetectScale(scaleCaption);
        string cyLabel = (currentYear ?? 0) > 0 ? currentYear!.ToString()! : "current";
        string pyLabel = (priorYear ?? 0) > 0 ? priorYear!.ToString()! : "prior";
        string? detectedCurrency = NumberNormalizer.DetectCurrency(currencyText ?? "");

        var valuesByYear = new Dictionary<string, Dictionary<string, double?>>
        {
            [cyLabel] = new(), [pyLabel] = new(),
        };
        var cells = new Dictionary<string, Dictionary<string, object>>();
        var extractedFlag = new Dictionary<string, bool>();

        foreach (var f in _spec.AllExtractable())
        {
            var byYear = new Dictionary<string, object>();
            bool anyRaw = false;
            foreach (var (label, raw) in new[] { (cyLabel, rawCurrent.GetValueOrDefault(f.Key)),
                                                 (pyLabel, rawPrior.GetValueOrDefault(f.Key)) })
            {
                if (!string.IsNullOrWhiteSpace(raw)) anyRaw = true;
                bool numeric = f.IsMonetary || f.Key == "average_number_of_persons_employed";
                if (!numeric)
                {
                    byYear[label] = new { raw, value = (double?)null, currency = (string?)null };
                    valuesByYear[label][f.Key] = null;
                    continue;
                }
                long ts = f.Key == "average_number_of_persons_employed" ? 1 : scale;
                var parsed = NumberNormalizer.Parse(raw, ts);
                double? value = SignRules.Resolve(f.OfficialName, raw, parsed.Value);
                byYear[label] = new { raw, value, currency = parsed.Currency ?? detectedCurrency };
                valuesByYear[label][f.Key] = value;
            }
            cells[f.Key] = byYear;
            extractedFlag[f.Key] = anyRaw;
        }

        Dictionary<string, double?> Flat(string label) => new(valuesByYear[label]);

        var cur = Flat(cyLabel);
        double? turnover = cur.GetValueOrDefault("revenue");
        double? bsTotal = cur.GetValueOrDefault("total_assets")
            ?? ((cur.GetValueOrDefault("total_fixed_assets") ?? 0) + (cur.GetValueOrDefault("current_assets") ?? 0));
        if (bsTotal == 0) bsTotal = null;
        int? emp = cur.GetValueOrDefault("average_number_of_persons_employed") is { } e ? (int)e : null;
        var size = CompanySizeClassifier.Classify(turnover, bsTotal, emp,
            (frameworkText ?? "") + " " + (scaleCaption ?? ""));
        string sizeKey = size.Size.ToString().ToLowerInvariant();

        var fieldList = _spec.AllExtractable().Select(f => (object)new
        {
            key = f.Key,
            section = f.Section,
            label = f.DisplayedName,
            official_name = f.OfficialName,
            fs_section = f.FsSection,
            applicable = f.Applicability.GetValueOrDefault(sizeKey, "").ToUpperInvariant(),
            extracted = extractedFlag[f.Key],
            values = cells[f.Key],
        }).ToList();

        var calculated = new Dictionary<string, object>();
        var validation = new Dictionary<string, object>();
        var sumMismatches = new List<string>();
        foreach (var label in new[] { cyLabel, pyLabel })
        {
            var flat = Flat(label);
            var derived = Calculations.DeriveStructural(flat);
            foreach (var (k, v) in derived) flat[k] = v;
            calculated[label] = new { derived, ratios = Calculations.ComputeRatios(flat) };

            var checks = Validation.ValidateYear(flat);
            validation[label] = new { summary = Validation.Summarize(checks), checks };
            sumMismatches.AddRange(checks.Where(c => c.Ok == false)
                .Select(c => $"{PrettyCheck(c.Name)} ({label}): the figure captured from the document is " +
                             $"{c.Expected:N0}; the individual lines we captured only sum to {c.Computed:N0}. " +
                             $"(The second number is OUR consistency calculation, not a figure from the document - " +
                             $"a gap usually means a line item we did not capture.)"));
        }

        var tables = TableValidator.Validate(tableRows, cyLabel, pyLabel);
        var tableMismatches = tables
            .Where(t => t.Status == "mismatch")
            .Select(t => $"Table '{t.Name}': " +
                         string.Join(" ", t.Checks.Where(c => c.Status == "mismatch")
                                                  .Select(c => $"[{c.Year}] {c.Message}")))
            .ToList();
        var tableChecks = tables.SelectMany(t => t.Checks).ToList();

        var requiredMissing = _spec.AllExtractable()
            .Where(f => f.Applicability.GetValueOrDefault(sizeKey, "").ToUpperInvariant() == "YES" && !extractedFlag[f.Key])
            .Select(f => f.DisplayedName).ToList();
        bool needsReview = requiredMissing.Any() || sumMismatches.Any() || tableMismatches.Any();

        int declaredApplicable = _spec.AllExtractable()
            .Count(f => f.Applicability.GetValueOrDefault(sizeKey, "").ToUpperInvariant() is "YES" or "OPTIONAL");
        int requiredCount = _spec.AllExtractable()
            .Count(f => f.Applicability.GetValueOrDefault(sizeKey, "").ToUpperInvariant() == "YES");
        int extractedCount = extractedFlag.Count(kv => kv.Value);

        return new
        {
            document_type = new { type = docType, label = docTypeLabel, confidence },
            company_size = new
            {
                size = sizeKey,
                framework = CompanySizeClassifier.FrameworkLabel(size.Framework),
                method = size.Method,
                reasons = size.Reasons,
                criteria = new { turnover, balance_sheet_total = bsTotal, employees = emp },
                thresholds = CompanySizeClassifier.ThresholdTable(),
            },
            review = new
            {
                needs_manual_review = needsReview,
                required_missing = requiredMissing,
                sum_mismatches = sumMismatches,
                table_mismatches = tableMismatches,
            },
            tables = new
            {
                summary = TableValidator.Summarize(tables),
                checks_passed = tableChecks.Count(c => c.Status == "ok"),
                checks_failed = tableChecks.Count(c => c.Status == "mismatch"),
                detected = tables,
            },
            currency = detectedCurrency ?? "unknown",
            scale = new { caption = scaleCaption ?? "", factor = scale },
            years = new[] { cyLabel, pyLabel },
            coverage = new { extracted = extractedCount, declared_for_size = declaredApplicable,
                             required = requiredCount, required_missing = requiredMissing.Count,
                             total_fields = fieldList.Count },
            fields = fieldList,
            calculated,
            validation,
            commentary,
            notes,
            cost,
            warnings,
        };
    }

    private static string PrettyCheck(string name) => name switch
    {
        "gross_profit" => "Gross profit = turnover − cost of sales",
        "operating_profit" => "Operating profit build-up",
        "profit_before_tax" => "Profit before tax build-up",
        "profit_for_year" => "Profit for the year = PBT − tax",
        "total_fixed_assets" => "Total fixed assets = tangible + intangible + financial",
        "net_assets" => "Net assets = fixed + current − creditors",
        "capital_equals_net_assets" => "Capital & reserves = net assets",
        "equity_reconciliation" => "Equity = share capital + reserves",
        _ => name.Replace('_', ' '),
    };
}
