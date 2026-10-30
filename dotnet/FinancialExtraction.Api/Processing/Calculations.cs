// Computes the values that are not printed in the document, such as Total Assets
// under FRS 102, and all the financial ratios (margins, returns, and so on).

namespace FinancialExtraction.Api.Processing;

public static class Calculations
{
    private static double? G(IReadOnlyDictionary<string, double?> v, string k) => v.GetValueOrDefault(k);

    private static double? Div(double? a, double? b) => (a is null || b is null || b == 0) ? null : a / b;
    private static double? Pct(double? x) => x is null ? null : Math.Round(x.Value * 100, 2);
    private static double? R(double? x, int nd = 2) => x is null ? null : Math.Round(x.Value, nd);

    public static Dictionary<string, double?> DeriveStructural(IReadOnlyDictionary<string, double?> v)
    {
        var outp = new Dictionary<string, double?>();

        if (G(v, "total_fixed_assets") is null)
        {
            var parts = new[] { "tangible_fixed_assets", "intangible_assets", "financial_assets" }
                .Select(k => G(v, k)).ToList();
            if (parts.Any(p => p is not null))
                outp["total_fixed_assets"] = parts.Sum(p => p ?? 0);
        }

        double? tfa = G(v, "total_fixed_assets") ?? outp.GetValueOrDefault("total_fixed_assets");
        double? ca = G(v, "current_assets");
        if (G(v, "total_assets") is null && (tfa is not null || ca is not null))
            outp["total_assets"] = (tfa ?? 0) + (ca ?? 0);

        return outp;
    }

    public static Dictionary<string, double?> ComputeRatios(IReadOnlyDictionary<string, double?> v)
    {
        double? revenue = G(v, "revenue");
        double? grossProfit = G(v, "gross_profit");
        double? operatingProfit = G(v, "operating_profit");
        double? pat = G(v, "profit_for_the_financial_year");
        double? equity = G(v, "capital_and_reserves") ?? G(v, "net_assets");
        double? totalAssets = G(v, "total_assets");
        double? currentAssets = G(v, "current_assets");
        double? stocks = G(v, "stocks");
        double? debtors = G(v, "debtors");
        double? curLiab = G(v, "creditors_amounts_falling_due_within_one_year");
        double? cos = G(v, "cost_of_sales");
        double? tradeCreditors = G(v, "trade_creditors");
        double? financeCosts = G(v, "finance_costs");
        double? dep = G(v, "depreciation_of_tangible_assets");
        double? amort = G(v, "amortisation_of_intangible_assets");
        double? longDebt = G(v, "creditors_amounts_falling_due_after_more_than_one_year");

        double? ebitda = operatingProfit is null ? null
            : operatingProfit + Math.Abs(dep ?? 0) + Math.Abs(amort ?? 0);

        double? AbsOrNull(double? x) => x is null ? null : Math.Abs(x.Value);

        var ratios = new Dictionary<string, double?>
        {
            ["gross_margin"] = Pct(Div(grossProfit, revenue)),
            ["operating_margin"] = Pct(Div(operatingProfit, revenue)),
            ["net_margin"] = Pct(Div(pat, revenue)),
            ["ebitda"] = ebitda,
            ["ebitda_margin"] = Pct(Div(ebitda, revenue)),
            ["return_on_equity"] = Pct(Div(pat, equity)),
            ["return_on_assets"] = Pct(Div(pat, totalAssets)),
            ["current_ratio"] = R(Div(currentAssets, AbsOrNull(curLiab))),
            ["quick_ratio"] = R(Div(
                (currentAssets is not null && stocks is not null) ? currentAssets - Math.Abs(stocks.Value) : null,
                AbsOrNull(curLiab))),
            ["net_working_capital"] = (currentAssets is not null && curLiab is not null)
                ? currentAssets - Math.Abs(curLiab.Value) : null,
            ["gearing_ratio"] = R(Div(AbsOrNull(longDebt), equity)),
            ["debtor_days"] = R(Div(debtors, revenue) * 365),
            ["creditor_days"] = R(Div(AbsOrNull(tradeCreditors), AbsOrNull(cos)) * 365),
            ["inventory_days"] = R(Div(AbsOrNull(stocks), AbsOrNull(cos)) * 365),
            ["interest_cover"] = R(Div(operatingProfit, AbsOrNull(financeCosts))),
        };
        return ratios.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
