// Takes the table rows the model found in the document and checks their arithmetic.
// A table is not always a flat list: a profit and loss account goes step by step
// (revenue and costs give the operating loss, then finance costs give the loss
// before tax, and so on). So we walk the rows in the order they are printed, and
// each time we meet a subtotal we check it against the previous subtotal plus the
// lines in between. A simple note table is the easy case, with only one step.
// Nothing is hard-coded: it works with any table, whatever the lines are called.

using System.Text.Json.Nodes;

namespace FinancialExtraction.Api.Processing;

public static class TableValidator
{
    // Current/Prior are the NORMALISED figures: the printed amount multiplied by the
    // table's scale (a "1,234" in a "€'000" table becomes 1 234 000). RawCurrent /
    // RawPrior keep the exact printed string (brackets included), so callers can
    // apply their own sign handling.
    public record Row(string Label, string Type, string? RawCurrent, string? RawPrior,
                      double? Current, double? Prior);

    public record Check(string Year, string At, double? StartingFrom, double? ComponentsSum,
                        double? Expected, double? Stated, double? Difference,
                        int ComponentCount, string Status, string Message);

    public record TableResult(string Name, string? Page, string? Currency, string? ScaleCaption,
                              long ScaleFactor, int RowCount, List<Row> Rows, List<Check> Checks, string Status);

    private sealed class Group
    {
        public string Name = "";
        public string? Page;
        public string? Currency;
        public string? Scale;
        public readonly List<(string Label, string Type, string? Cur, string? Pri)> Rows = new();
    }

    private const string Ok = "ok";
    private const string Mismatch = "mismatch";
    private const string NotChecked = "not_checked";

    // The rows and the totals are compared exactly as printed, so the only slack we
    // allow is rounding: a table shown in €'000 can round every line by half a unit.
    // The tolerance therefore grows with the number of lines, never with the amount,
    // otherwise a percentage of a large total would hide a real error.
    private static bool Close(double expected, double stated, int componentCount)
        => Math.Abs(expected - stated) <= Math.Max(1.0, 0.5 * componentCount);

    // A figure past this size is not a real amount in a set of company accounts, it is
    // the sign that several columns of a wide table got glued into one cell. We drop it
    // instead of pretending to check it; the raw text stays visible for a human to see.
    private const double MaxPlausibleAmount = 1e15;

    private static double? Sane(double? v)
        => v is { } d && !double.IsNaN(d) && Math.Abs(d) < MaxPlausibleAmount ? v : null;

    private static string? Str(JsonNode? item, string key)
    {
        var v = item?[key];
        if (v is null) return null;
        var s = v.ToString().Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static List<TableResult> Validate(JsonArray? rows, string currentYearLabel, string priorYearLabel)
    {
        var results = new List<TableResult>();
        if (rows is null) return results;

        // 1. Group the raw rows by table, remembering the currency and scale the model
        //    read from each table's own column headings (repeated on every row).
        var grouped = new List<Group>();
        foreach (var item in rows)
        {
            var name = Str(item, "table_name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var type = (Str(item, "row_type") ?? "component").ToLowerInvariant();
            if (type is not ("component" or "total" or "subtotal" or "memo")) type = "component";

            var g = grouped.FirstOrDefault(x => x.Name == name);
            if (g is null)
            {
                g = new Group { Name = name!, Page = Str(item, "page_number") };
                grouped.Add(g);
            }
            g.Currency ??= Str(item, "currency");
            g.Scale ??= Str(item, "scale");
            g.Rows.Add((Str(item, "row_label") ?? "", type, Str(item, "current_year_value"), Str(item, "prior_year_value")));
        }

        // 2. Normalise each table's figures with its own scale, then check the maths.
        foreach (var g in grouped)
        {
            long scaleFactor = NumberNormalizer.DetectScale(g.Scale);
            // The scale caption often carries the symbol too ("€'000"), so fall back to
            // it, and finally to the figures themselves, when the currency field is blank.
            string? currency = NumberNormalizer.DetectCurrency(g.Currency ?? "")
                ?? NumberNormalizer.DetectCurrency(g.Scale ?? "")
                ?? NumberNormalizer.DetectCurrency(string.Join(" ", g.Rows.Select(r => r.Cur)));

            var tableRows = g.Rows.Select(r => new Row(
                r.Label, r.Type, r.Cur, r.Pri,
                Sane(NumberNormalizer.Parse(r.Cur, scaleFactor).Value),
                Sane(NumberNormalizer.Parse(r.Pri, scaleFactor).Value))).ToList();

            var checks = new List<Check>();
            checks.AddRange(Walk(currentYearLabel, tableRows, r => r.Current));
            checks.AddRange(Walk(priorYearLabel, tableRows, r => r.Prior));

            string status =
                checks.Any(c => c.Status == Mismatch) ? Mismatch
                : checks.Any(c => c.Status == Ok) ? Ok
                : NotChecked;

            results.Add(new TableResult(g.Name, g.Page, currency, string.IsNullOrWhiteSpace(g.Scale) ? null : g.Scale,
                scaleFactor, tableRows.Count, tableRows, checks, status));
        }
        return results;
    }

    // Walks one column of the table from top to bottom. Components accumulate; each
    // subtotal or total closes the current step and becomes the base for the next one.
    private static List<Check> Walk(string year, List<Row> rows, Func<Row, double?> pick)
    {
        var checks = new List<Check>();
        double? running = null;
        var pending = new List<double>();

        foreach (var row in rows)
        {
            if (row.Type == "memo") continue;

            var value = pick(row);
            if (row.Type == "component")
            {
                if (value is not null) pending.Add(value.Value);
                continue;
            }
            if (value is null) continue;

            // An opening balance: nothing has been added yet, so it is the starting point.
            if (running is null && pending.Count == 0)
            {
                running = value;
                continue;
            }

            double componentsSum = Math.Round(pending.Sum(), 2);
            double expected = Math.Round((running ?? 0) + componentsSum, 2);
            double stated = value.Value;
            double diff = Math.Round(stated - expected, 2);
            bool ok = Close(expected, stated, pending.Count);

            string label = string.IsNullOrWhiteSpace(row.Label) ? row.Type : row.Label;
            string basis = running is null
                ? $"the {pending.Count} line items above it add up to {componentsSum:N0}"
                : $"the previous running figure of {running:N0} plus the {pending.Count} line items " +
                  $"above it gives {expected:N0}";

            checks.Add(new Check(year, label, running, componentsSum, expected, stated, diff,
                pending.Count, ok ? Ok : Mismatch,
                ok
                    ? $"'{label}': {basis}, which matches the printed {stated:N0}."
                    : $"'{label}': {basis}, but the document prints {stated:N0} (difference {diff:N0})."));

            running = stated;
            pending.Clear();
        }
        return checks;
    }

    public static object Summarize(List<TableResult> tables) => new
    {
        tables_found = tables.Count,
        passed = tables.Count(t => t.Status == Ok),
        failed = tables.Count(t => t.Status == Mismatch),
        unchecked_tables = tables.Count(t => t.Status == NotChecked),
        checks_run = tables.Sum(t => t.Checks.Count),
        needs_review = tables.Where(t => t.Status == Mismatch).Select(t => t.Name).ToList(),
    };
}
