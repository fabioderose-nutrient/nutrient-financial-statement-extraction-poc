// Checks that the extracted numbers are consistent.
// It adds up the detail lines, compares them with the extracted subtotal, and
// reports any difference so that a person can check it by hand.

namespace FinancialExtraction.Api.Processing;

public static class Validation
{
    private enum Role { Income, Subtotal, Cost }

    private record Component(string Key, Role Role);
    private record Rule(string Name, string TargetKey, Component[] Components);

    private static readonly Rule[] Rules =
    {
        new("gross_profit", "gross_profit", new[]
        {
            new Component("revenue", Role.Income), new Component("cost_of_sales", Role.Cost),
        }),
        new("operating_profit", "operating_profit", new[]
        {
            new Component("gross_profit", Role.Subtotal), new Component("other_operating_income", Role.Income),
            new Component("other_operating_charges", Role.Cost), new Component("administrative_expenses", Role.Cost),
        }),
        new("profit_before_tax", "profit_on_ordinary_activities_before_taxation", new[]
        {
            new Component("operating_profit", Role.Subtotal),
            new Component("finance_income_interest_receivable", Role.Income),
            new Component("finance_costs", Role.Cost),
        }),
        new("profit_for_year", "profit_for_the_financial_year", new[]
        {
            new Component("profit_on_ordinary_activities_before_taxation", Role.Subtotal),
            new Component("tax_on_profit_on_ordinary_activities", Role.Cost),
        }),
        new("total_fixed_assets", "total_fixed_assets", new[]
        {
            new Component("tangible_fixed_assets", Role.Income), new Component("intangible_assets", Role.Income),
            new Component("financial_assets", Role.Income),
        }),
        new("net_assets", "net_assets", new[]
        {
            new Component("total_fixed_assets", Role.Subtotal), new Component("current_assets", Role.Income),
            new Component("creditors_amounts_falling_due_within_one_year", Role.Cost),
            new Component("creditors_amounts_falling_due_after_more_than_one_year", Role.Cost),
        }),
        new("capital_equals_net_assets", "net_assets", new[]
        {
            new Component("capital_and_reserves", Role.Subtotal),
        }),
        new("equity_reconciliation", "capital_and_reserves", new[]
        {
            new Component("called_up_share_capital", Role.Income),
            new Component("revaluation_reserve", Role.Income),
            new Component("profit_and_loss_account_reserve", Role.Subtotal),
        }),
    };

    public record CheckResult(string Name, bool? Ok, double? Expected, double? Computed, double? Difference, string Message);

    private static double Signed(double v, Role role) => role == Role.Cost ? -Math.Abs(v) : v;

    private static bool Close(double a, double b, double absEps = 1.0, double relEps = 0.03)
        => Math.Abs(a - b) <= Math.Max(absEps, relEps * Math.Max(Math.Abs(a), Math.Abs(b)));

    public static List<CheckResult> ValidateYear(IReadOnlyDictionary<string, double?> values)
    {
        var results = new List<CheckResult>();
        foreach (var rule in Rules)
        {
            double? target = values.GetValueOrDefault(rule.TargetKey);
            var parts = rule.Components.Select(c => (c.Role, V: values.GetValueOrDefault(c.Key))).ToList();
            if (target is null || parts.Any(p => p.V is null))
            {
                results.Add(new(rule.Name, null, target, null, null, "Insufficient data (missing component or target)."));
                continue;
            }
            double computed = Math.Round(parts.Sum(p => Signed(p.V!.Value, p.Role)), 2);
            bool ok = Close(computed, target.Value);
            results.Add(new(rule.Name, ok, target, computed, Math.Round(target.Value - computed, 2),
                ok ? "OK" : "MISMATCH - flag for manual review."));
        }
        return results;
    }

    public static object Summarize(List<CheckResult> results) => new
    {
        total_checks = results.Count,
        passed = results.Count(r => r.Ok == true),
        failed = results.Count(r => r.Ok == false),
        skipped = results.Count(r => r.Ok is null),
        needs_review = results.Where(r => r.Ok == false).Select(r => r.Name).ToList(),
    };
}
