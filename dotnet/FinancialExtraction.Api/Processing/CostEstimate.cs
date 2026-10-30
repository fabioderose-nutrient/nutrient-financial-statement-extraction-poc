// Estimates the tokens and the cost of one document.
// It uses the length of the text and a small price table per model.

namespace FinancialExtraction.Api.Processing;

public static class CostEstimate
{

    public static readonly Dictionary<string, (double Input, double Output)> Pricing = new()
    {
        ["gpt-4o"]     = (2.50, 10.00),
        ["gpt-5-mini"] = (0.25, 2.00),
        ["gpt-5-nano"] = (0.05, 0.40),
        ["o3-mini"]    = (1.10, 4.40),
    };

    public static int EstimateTokens(string text) => Math.Max(1, (int)Math.Round(text.Length / 4.0));

    public record Report(
        string Model, int InputTokens, int OutputTokens,
        double ExtractionSeconds, double EstimatedCostUsd, string Note);

    public static Report Build(string model, string documentText, int extractedFieldCount,
                               double extractionSeconds)
    {
        int inTok = EstimateTokens(documentText);
        int outTok = Math.Max(50, extractedFieldCount * 25);
        double cost = 0;
        if (Pricing.TryGetValue(model, out var rate))
            cost = Math.Round(inTok / 1_000_000.0 * rate.Input + outTok / 1_000_000.0 * rate.Output, 4);
        return new Report(model, inTok, outTok, Math.Round(extractionSeconds, 1), cost,
            "Token counts estimated from text size (XtractFlow exposes LLM time, not raw tokens). " +
            "Pricing indicative, confirm against your OpenAI contract.");
    }
}
