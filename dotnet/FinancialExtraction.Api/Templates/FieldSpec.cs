using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinancialExtraction.Api.Templates;

public class FieldSpec
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("section")] public string Section { get; set; } = "";
    [JsonPropertyName("displayed_name")] public string DisplayedName { get; set; } = "";
    [JsonPropertyName("official_name")] public string OfficialName { get; set; } = "";
    [JsonPropertyName("alternatives")] public List<string> Alternatives { get; set; } = new();
    [JsonPropertyName("applicability")] public Dictionary<string, string> Applicability { get; set; } = new();
    [JsonPropertyName("fs_section")] public string FsSection { get; set; } = "";
    [JsonPropertyName("calculated")] public bool Calculated { get; set; }

    public string? RichDescription { get; set; }

    public bool IsMonetary =>
        Key is not ("reporting_currency" or "financial_year_end_date"
                    or "average_number_of_persons_employed")
        && Section is not "Ratios";

    public string SemanticDescription()
    {
        if (!string.IsNullOrWhiteSpace(RichDescription))
        {
            var tail = IsMonetary
                ? " If the statements show separate 'Group'/'Consolidated' and 'Company' columns, take ONLY the Company (individual entity) figure, never the Group/consolidated one. Return the amount exactly as printed, keeping any brackets, commas, currency symbol and the thousands/millions scale caption."
                : "";
            return RichDescription!.Trim() + tail;
        }

        var alts = Alternatives.Count > 0
            ? $" It is typically labelled '{OfficialName}' or one of: {string.Join(", ", Alternatives)}."
            : $" It is typically labelled '{OfficialName}'.";
        var where = string.IsNullOrWhiteSpace(FsSection) ? "" : $" Usually found in: {FsSection}.";
        var kind = Section switch
        {
            "Profit & Loss Account" => "a line item on the company's profit and loss account",
            "Balance Sheet" => "a line item on the company's balance sheet",
            "Cash Flow" => "a line item on the company's cash flow statement",
            _ => "a figure disclosed in the company's financial statements",
        };
        var literal = IsMonetary
            ? " Match only the value explicitly labelled with this caption or a recognised synonym, not a similar-looking figure. If both 'Group'/'Consolidated' and 'Company' columns are shown, take ONLY the Company (individual entity) figure. Return the amount exactly as printed, keeping any brackets, commas, currency symbol and the thousands/millions scale caption."
            : "";
        return $"The '{OfficialName}' - {kind}.{alts}{where}{literal}".Trim();
    }
}

public class FieldSpecFile
{
    [JsonPropertyName("fields")] public List<FieldSpec> Fields { get; set; } = new();

    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static FieldSpecFile Load(string path)
    {
        var file = JsonSerializer.Deserialize<FieldSpecFile>(File.ReadAllText(path), Opts) ?? new FieldSpecFile();

        var descPath = Path.Combine(Path.GetDirectoryName(path) ?? ".", "field_descriptions.json");
        if (File.Exists(descPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(descPath));
            var map = new Dictionary<string, string>();
            foreach (var p in doc.RootElement.EnumerateObject())
                if (!p.Name.StartsWith("_") && p.Value.ValueKind == JsonValueKind.String)
                    map[p.Name] = p.Value.GetString() ?? "";
            foreach (var f in file.Fields)
                if (map.TryGetValue(f.Key, out var d)) f.RichDescription = d;
        }
        return file;
    }

    public IEnumerable<FieldSpec> ExtractableFor(string size, bool includeOptional = true)
    {
        var allowed = includeOptional ? new[] { "YES", "OPTIONAL" } : new[] { "YES" };
        return Fields.Where(f => !f.Calculated
            && f.Applicability.TryGetValue(size, out var v)
            && allowed.Contains(v.ToUpperInvariant()));
    }

    public IEnumerable<FieldSpec> AllExtractable() => Fields.Where(f => !f.Calculated);
}
