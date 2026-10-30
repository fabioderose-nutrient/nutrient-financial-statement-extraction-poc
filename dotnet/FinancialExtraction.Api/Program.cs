using FinancialExtraction.Api.Processing;
using FinancialExtraction.Api.Templates;
using GdPicture14;
using XTractFlow.API;
using XTractFlow.API.Component;
using XTractFlow.API.Document;
using XTractFlow.API.LLM.Providers;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 200 * 1024 * 1024);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors();

// Cross-origin isolation for the Nutrient Web SDK WebAssembly engine. The SDK is
// loaded from the Nutrient CDN, so the embedder policy is "credentialless": it keeps
// the page isolated while still allowing the CDN scripts, workers and WASM to load.
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    ctx.Response.Headers["Cross-Origin-Embedder-Policy"] = "credentialless";
    await next();
});

var frontendRoot = ResolveFrontend(builder.Environment.ContentRootPath);
var frontendFiles = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(frontendRoot);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = frontendFiles });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = frontendFiles,
    // The demo's own HTML/JS/CSS must never be cached, otherwise the browser keeps
    // running an old app.js after an update.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-store, must-revalidate",
});

static string ResolveFrontend(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "frontend");
        if (Directory.Exists(candidate)) return candidate;
    }
    return Path.Combine(start, "frontend");
}

var jsonOpts = new System.Text.Json.JsonSerializerOptions
{
    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
};

string SpecPath() => Path.Combine(AppContext.BaseDirectory, "Spec", "fields_spec.json");
var spec = FieldSpecFile.Load(SpecPath());
var resultBuilder = new ResultBuilder(spec);

// License keys come from environment variables only. Leave them empty to run the
// Nutrient SDKs in evaluation mode.
string GdKey() => (Environment.GetEnvironmentVariable("GDPICTURE_LICENSE_KEY") ?? "").Trim();
string WebKey() => (Environment.GetEnvironmentVariable("NUTRIENT_WEB_LICENSE_KEY") ?? "").Trim();
string? OpenAiKey() => Environment.GetEnvironmentVariable("OPENAI_API_KEY");

// gpt-4o is the default: it never trips the OpenAI SDK's 100s per-request timeout
// (gpt-5-mini reasoning does, and fails outright on dense documents), it is far
// faster, and with the table-splitting rules in the template its structural
// accuracy is close. Set OPENAI_MODEL=gpt-5-mini for slightly cleaner
// structure on small documents where the timeout is not a risk.
var ModelChoice = (Environment.GetEnvironmentVariable("OPENAI_MODEL") is { Length: > 0 } modelEnv ? modelEnv : "gpt-4o").ToLowerInvariant();
OpenAIProvider.OpenAILargeLanguageModel ModelEnum() => ModelChoice switch
{
    "gpt-4o" => OpenAIProvider.OpenAILargeLanguageModel.Gpt4o,
    "o3-mini" => OpenAIProvider.OpenAILargeLanguageModel.o3Mini,
    "gpt-5-nano" => OpenAIProvider.OpenAILargeLanguageModel.Gpt5Nano,
    _ => OpenAIProvider.OpenAILargeLanguageModel.Gpt5Mini,
};

// One attempt, no automatic retry: a pass that hits the inference timeout should
// fail fast rather than silently double its wall time by trying again.
Configuration.RetryCount = 1;
var inferenceTimeout = TimeSpan.FromSeconds(
    int.TryParse(Environment.GetEnvironmentVariable("LLM_TIMEOUT_SECONDS"), out var ts) && ts > 0 ? ts : 600);
var maxParallelBatches =
    int.TryParse(Environment.GetEnvironmentVariable("MAX_PARALLEL_BATCHES"), out var mp) && mp > 0 ? mp : 3;

// OCR resources (latin.traineddata) are only needed for scanned or image-only PDFs.
// Use OCR_RESOURCES_PATH, or the GdPicture.Resources NuGet package if it is in the
// local NuGet cache.
string? ResolveResourcesFolder()
{
    var candidates = new List<string>();
    var env = Environment.GetEnvironmentVariable("OCR_RESOURCES_PATH");
    if (!string.IsNullOrWhiteSpace(env)) candidates.Add(Path.GetFullPath(env));
    var nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } np
        ? np
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    var pkg = Path.Combine(nugetRoot, "gdpicture.resources");
    if (Directory.Exists(pkg))
        foreach (var v in Directory.GetDirectories(pkg).OrderByDescending(x => x))
            candidates.Add(Path.Combine(v, "runtimes", "any", "native"));
    foreach (var c in candidates)
        if (Directory.Exists(c) &&
            (File.Exists(Path.Combine(c, "latin.traineddata")) || File.Exists(Path.Combine(c, "Latin.traineddata"))))
            return c;
    return null;
}
var resourcesFolder = ResolveResourcesFolder();
if (resourcesFolder != null)
{
    Configuration.ResourcesFolder = resourcesFolder;
    app.Logger.LogInformation("OCR resources folder: {Path}", resourcesFolder);
}
else app.Logger.LogWarning("OCR resources folder not found (set OCR_RESOURCES_PATH). Scanned or image-only PDFs will fail.");

Guid? llmProvider = !string.IsNullOrWhiteSpace(OpenAiKey())
    ? Configuration.RegisterLLMProvider(new OpenAIProvider(OpenAiKey()!, ModelEnum()))
    : (Guid?)null;

// GdPicture (used by XtractFlow) must always be registered: an empty key turns on
// evaluation mode (watermarked output). Without the call, every GdPicture feature
// throws LicenseMissingFeatureException.
var licenseDiag = new Dictionary<string, object?>();
bool gdLicensed = true;
string? gdLicenseError = null;
try
{
    Configuration.RegisterGdPictureKey(GdKey());
    licenseDiag["mode"] = GdKey().Length > 0 ? "licensed" : "evaluation";
    licenseDiag["registered_via"] = "Configuration.RegisterGdPictureKey";
    app.Logger.LogInformation(GdKey().Length > 0
        ? "GdPicture key registered via Configuration.RegisterGdPictureKey."
        : "GDPICTURE_LICENSE_KEY is empty: XtractFlow and GdPicture run in evaluation mode.");
}
catch (Exception ex)
{
    gdLicensed = false;
    licenseDiag["error"] = ex.Message;
    gdLicenseError = ex.Message;
    app.Logger.LogWarning("GdPicture license registration failed: {Msg}", ex.Message);
}

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    gdpicture_key_set = GdKey().Length > 0,
    openai_key_set = !string.IsNullOrWhiteSpace(OpenAiKey()),
    model = ModelChoice,
    fields_loaded = spec.Fields.Count,
}));

// The Web SDK license key for the browser. Empty = evaluation mode (watermark).
app.MapGet("/api/config", () => Results.Json(new { licenseKey = WebKey() }));

app.MapGet("/api/license", () => Results.Json(licenseDiag, jsonOpts));

var jobs = new System.Collections.Concurrent.ConcurrentDictionary<string, ProcessProgress>();

app.MapGet("/api/progress", (HttpRequest r) =>
{
    var id = r.Query["job"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(id) || !jobs.TryGetValue(id, out var p))
        return Results.Json(new { known = false }, jsonOpts);
    return Results.Json(new
    {
        known = true,
        stage = p.Stage,
        total = p.Total,
        done = p.Done,
        finished = p.Finished,
        elapsed_seconds = Math.Round((DateTime.UtcNow - p.Started).TotalSeconds, 1),
    }, jsonOpts);
});

app.MapGet("/api/templates", () =>
{
    var templates = ExtractionTemplates.BuildAll(spec).Select(t => new
    {
        name = t.Name,
        identifier = t.Identifier,
        semantic_description = t.SemanticDescription,
        field_count = t.Fields?.Count ?? 0,
        fields = t.Fields?.Select(f => new { name = f.Name, format = f.Format.ToString(), description = f.SemanticDescription }),
    });
    return Results.Ok(templates);
});

// Providers registered for keys supplied per request (field "openai_key"),
// cached so each distinct key is only registered once.
var requestProviders = new System.Collections.Concurrent.ConcurrentDictionary<string, Guid>();

app.MapPost("/api/process", async (HttpRequest req) =>
{
    if (!req.HasFormContentType || req.Form.Files.Count == 0)
        return Results.BadRequest(new { error = "Upload a PDF as multipart/form-data (field 'file')." });

    // A key sent with the request wins over the OPENAI_API_KEY environment variable.
    var userKey = req.Form["openai_key"].FirstOrDefault()?.Trim();
    Guid? provider = llmProvider;
    if (!string.IsNullOrWhiteSpace(userKey))
    {
        try
        {
            provider = requestProviders.GetOrAdd(userKey,
                k => Configuration.RegisterLLMProvider(new OpenAIProvider(k, ModelEnum())));
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = "The supplied OpenAI key was rejected.", detail = ex.Message });
        }
    }
    if (provider is null)
        return Results.BadRequest(new { error = "No OpenAI key available. Set OPENAI_API_KEY on the server, or send it in the 'openai_key' form field." });

    if (!gdLicensed)
        return Results.BadRequest(new
        {
            error = "GdPicture / XtractFlow license is not valid, so document processing is disabled.",
            detail = gdLicenseError,
        });

    bool useOcr = req.Query["ocr"] != "false";
    var file = req.Form.Files[0];
    var warnings = new List<string>();
    var forced = req.Query["template"].FirstOrDefault() ?? ExtractionTemplates.FinancialStatements;

    ProcessProgress? prog = null;
    var jobId = req.Query["job"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(jobId))
    {
        foreach (var stale in jobs.Where(kv => kv.Value.Started < DateTime.UtcNow.AddMinutes(-30)).ToList())
            jobs.TryRemove(stale.Key, out _);
        prog = new ProcessProgress { Stage = "Uploading document" };
        jobs[jobId] = prog;
    }

    var tempPath = Path.Combine(Path.GetTempPath(), $"fse_{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
    await using (var s = file.OpenReadStream())
    await using (var fsOut = File.Create(tempPath))
        await s.CopyToAsync(fsOut);

    async Task<(Dictionary<string, string?> raw, XTractFlow.API.Result.ProcessorResult res, List<string> warns)> Run(DocumentTemplate tpl)
    {
        var processor = new DocumentProcessor(provider.Value)
        {
            LLMInferenceTimeout = inferenceTimeout,
            EnableOrientationDetection = useOcr,
        };
        var comp = new ProcessorComponent { EnableClassifier = false, EnableFieldsExtraction = true, EnableClassifierRefiner = false };
        comp.AddTemplate(tpl);
        comp.RestrictTemplate = true;
        var res = await processor.ProcessAsync(tempPath, comp, req.HttpContext.RequestAborted);
        if (prog is not null)
        {
            int done = Interlocked.Increment(ref prog.Done);
            prog.Stage = $"Reading the document ({done} of {prog.Total} passes done)";
        }
        var d = new Dictionary<string, string?>();
        var w = new List<string>();
        if (res.ExtractedFields != null)
            foreach (var f in res.ExtractedFields)
            {
                d[f.FieldName] = f.Value?.Value?.ToString();
                if (f.ValidationState == XTractFlow.API.Result.FieldValidationState.VerificationNeeded)
                    w.Add($"Field '{f.FieldName}' needs manual verification.");
            }
        return (d, res, w);
    }

    try
    {

        if (forced != ExtractionTemplates.FinancialStatements)
        {
            var formTpl = ExtractionTemplates.BuildAll(spec).FirstOrDefault(t => t.Name == forced);
            if (formTpl is null)
                return Results.BadRequest(new { error = $"Unknown template '{forced}'." });
            if (prog is not null) { prog.Total = 1; prog.Stage = "Reading the document (0 of 1 passes done)"; }
            var (fraw, fres, fwarns) = await Run(formTpl);
            warnings.AddRange(fwarns);

            string Prettify(string k) => System.Globalization.CultureInfo.CurrentCulture.TextInfo
                .ToTitleCase(k.Replace('_', ' '));
            int fExtracted = 0;
            var formFields = (formTpl.Fields ?? Array.Empty<XTractFlow.API.Document.TemplateField>()).Select(tf =>
            {
                var val = fraw.GetValueOrDefault(tf.Name);
                bool isArray = tf.Format == XTractFlow.API.Document.Fields.FieldDataFormat.Array;
                if (isArray)
                {

                    System.Text.Json.Nodes.JsonNode? items = null;
                    int count = 0;
                    if (!string.IsNullOrWhiteSpace(val))
                        try { if (System.Text.Json.Nodes.JsonNode.Parse(val) is System.Text.Json.Nodes.JsonArray a) { items = a; count = a.Count; } }
                        catch { }
                    if (count > 0) fExtracted++;
                    return (object)new
                    {
                        key = tf.Name, label = Prettify(tf.Name), is_array = true,
                        item_fields = (tf.ArrayItemFields ?? new List<XTractFlow.API.Document.TemplateField>()).Select(x => x.Name).ToList(),
                        count, extracted = count > 0, items,
                    };
                }
                bool ex = !string.IsNullOrWhiteSpace(val);
                if (ex) fExtracted++;
                return (object)new { key = tf.Name, label = Prettify(tf.Name), is_array = false, extracted = ex, value = val };
            }).ToList();

            var sizeText = ((fraw.GetValueOrDefault("size_exemption") ?? "") + " " + (fraw.GetValueOrDefault("company_type") ?? "")).ToLowerInvariant();
            string formSize = sizeText.Contains("micro") ? "micro"
                : sizeText.Contains("small") ? "small"
                : sizeText.Contains("medium") ? "medium"
                : sizeText.Contains("large") ? "large" : "unknown";
            var sizeReason = formSize == "unknown"
                ? "Company size is not stated on this form - it is determined from financial statements."
                : $"Derived from the size exemption/company type declared on the return: \"{fraw.GetValueOrDefault("size_exemption") ?? fraw.GetValueOrDefault("company_type")}\".";

            return Results.Json(new
            {
                document_type = new { type = forced.ToLowerInvariant().Replace(' ', '_'), label = forced,
                                      confidence = ConfidenceLabel(fres.Confidence) },
                is_form = true,
                company = new { name = fraw.GetValueOrDefault("company_name"), number = fraw.GetValueOrDefault("company_number") },
                company_size = new { size = formSize, method = "form_declaration", reasons = new[] { sizeReason } },
                coverage = new { extracted = fExtracted, declared_for_size = formFields.Count, total_fields = formFields.Count },
                form_fields = formFields,
                cost = new
                {
                    model = ModelChoice,
                    extraction_seconds = Math.Round((fres.ExtractionLLMTimeSpent ?? TimeSpan.Zero).TotalSeconds, 1),
                },
                warnings,
            }, jsonOpts);
        }

        bool onlyTables = req.Query["only"] == "tables";
        bool withCommentary = req.Query["commentary"] != "false" && !onlyTables;
        var batches = new List<DocumentTemplate>();

        if (!onlyTables)
        {
            var sectionGroups = spec.AllExtractable().GroupBy(f => f.Section).ToList();
            for (int i = 0; i < sectionGroups.Count; i++)
                batches.Add(ExtractionTemplates.BuildFinancialStatements(
                    spec, sectionGroups[i].ToList(), includeCommentary: false, includeMeta: i == 0));
            if (withCommentary)
                batches.Add(ExtractionTemplates.BuildFinancialStatements(
                    spec, new List<FieldSpec>(), includeCommentary: true, includeMeta: false));
        }

        bool withTables = req.Query["tables"] != "false" || onlyTables;
        if (withTables)
        {
            batches.Add(ExtractionTemplates.BuildTableValidation(ExtractionTemplates.ScopeStatements));
            batches.Add(ExtractionTemplates.BuildTableValidation(ExtractionTemplates.ScopeNotes));
        }

        if (prog is not null)
        {
            prog.Total = batches.Count;
            prog.Stage = $"Reading the document (0 of {batches.Count} passes done)";
        }
        // Firing every pass at once makes them fight for the same LLM budget and the
        // slowest ones start timing out, so we keep a few in flight at a time.
        using var gate = new SemaphoreSlim(maxParallelBatches);
        bool HasTables((Dictionary<string, string?> raw, XTractFlow.API.Result.ProcessorResult res, List<string> warns) r)
            => r.raw.Any(kv => !string.IsNullOrWhiteSpace(kv.Value) && kv.Value.Contains('{'));
        async Task<(Dictionary<string, string?> raw, XTractFlow.API.Result.ProcessorResult res, List<string> warns)> RunGated(DocumentTemplate tpl)
        {
            await gate.WaitAsync(req.HttpContext.RequestAborted);
            try
            {
                var result = await Run(tpl);
                // A note band occasionally comes back empty (model variance). Since the
                // bands do not overlap, an empty one silently drops a whole range of
                // notes, so retry it once before giving up on those tables.
                if (tpl.Name.StartsWith(ExtractionTemplates.TableValidation) && !HasTables(result))
                {
                    result = await Run(tpl);
                    if (!HasTables(result)) warnings.Add($"Batch '{tpl.Name}' returned no tables after a retry.");
                }
                return result;
            }
            finally { gate.Release(); }
        }

        var batchResults = await Task.WhenAll(batches.Select(RunGated));
        if (prog is not null) prog.Stage = "Checking the arithmetic of every table";
        var raw = new Dictionary<string, string?>();
        foreach (var (r, _, w) in batchResults)
        {
            foreach (var kv in r) if (!string.IsNullOrWhiteSpace(kv.Value)) raw[kv.Key] = kv.Value;
            warnings.AddRange(w);
        }
        var result = batchResults[0].res;
        double extractionSeconds = batchResults.Max(b => (b.res.ExtractionLLMTimeSpent ?? TimeSpan.Zero).TotalSeconds);
        int populated = raw.Count(kv => !string.IsNullOrWhiteSpace(kv.Value));
        warnings.Add($"model={ModelChoice}, batches={batches.Count}, populated={populated}");

        var cy = new Dictionary<string, string?>();
        var py = new Dictionary<string, string?>();
        foreach (var f in spec.AllExtractable())
        {
            cy[f.Key] = raw.GetValueOrDefault(f.Key);
            py[f.Key] = raw.GetValueOrDefault(f.Key + ExtractionTemplates.PriorSuffix);
        }

        var commentary = new Dictionary<string, ResultBuilder.CommentaryBlock>();
        foreach (var (key, _, _) in ExtractionTemplates.Commentaries)
            commentary[key] = new(raw.GetValueOrDefault(key), raw.GetValueOrDefault(key + ExtractionTemplates.SentimentSuffix), null);

        System.Text.Json.Nodes.JsonNode? notes = null;
        var notesRaw = raw.GetValueOrDefault("notes");
        if (!string.IsNullOrWhiteSpace(notesRaw))
            try { if (System.Text.Json.Nodes.JsonNode.Parse(notesRaw) is System.Text.Json.Nodes.JsonArray a) notes = a; } catch { }

        System.Text.Json.Nodes.JsonArray? tableRows = null;
        var seenTableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in ExtractionTemplates.AllTableRowFields())
        {
            var scopeRaw = raw.GetValueOrDefault(field);
            if (string.IsNullOrWhiteSpace(scopeRaw)) continue;
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(scopeRaw) is not System.Text.Json.Nodes.JsonArray arr) continue;
                tableRows ??= new System.Text.Json.Nodes.JsonArray();
                // Dedupe by TABLE (all rows of one table share a name): if a whole table
                // was already brought in by an earlier band, skip every row of it here;
                // keep all the rows of tables new to this pass.
                var namesThisBand = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in arr.ToList())
                {
                    var tn = node?["table_name"]?.ToString()?.Trim() ?? "";
                    if (tn.Length > 0 && seenTableNames.Contains(tn)) continue;
                    if (tn.Length > 0) namesThisBand.Add(tn);
                    arr.Remove(node);
                    tableRows.Add(node);
                }
                foreach (var n in namesThisBand) seenTableNames.Add(n);
            }
            catch { }
        }

        int? cyYear = YearFromDate(raw.GetValueOrDefault("financial_year_end_date"));
        int? pyYear = YearFromDate(raw.GetValueOrDefault("financial_year_end_date" + ExtractionTemplates.PriorSuffix))
                      ?? (cyYear is { } y ? y - 1 : null);

        var report = CostEstimate.Build(ModelChoice, string.Join(" ", raw.Values), populated,
            extractionSeconds);
        var cost = new
        {
            model = report.Model,
            total_input_tokens = report.InputTokens,
            total_output_tokens = report.OutputTokens,
            estimated_cost_usd = report.EstimatedCostUsd,
            extraction_seconds = report.ExtractionSeconds,
            note = report.Note,
        };

        var payload = resultBuilder.Build(cy, py,
            raw.GetValueOrDefault("presentation_scale"),
            raw.GetValueOrDefault("reporting_framework"),
            raw.GetValueOrDefault("reporting_currency"),
            "financial_statements", ExtractionTemplates.FinancialStatements,
            ConfidenceLabel(result.Confidence), commentary, cost, cyYear, pyYear, warnings, notes, tableRows);

        return Results.Json(payload, jsonOpts);
    }
    catch (Exception ex) when (ex.GetType().Name.Contains("License") || ex.Message.Contains("feature"))
    {
        return Results.Json(new
        {
            error = "GdPicture/XtractFlow license required.",
            detail = ex.Message,
            hint = "Set GDPICTURE_LICENSE_KEY to a license that includes the XtractFlow feature, then restart.",
        }, jsonOpts, statusCode: 402);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Processing failed for {File}", file.FileName);
        return Results.Json(new { error = "Processing failed.", detail = ex.Message }, jsonOpts, statusCode: 500);
    }
    finally
    {
        if (prog is not null) { prog.Stage = "Done"; prog.Finished = true; }
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch {  }
    }
});

app.Run();

static string ConfidenceLabel(double? c) => c is null ? "unknown" : c >= 0.75 ? "high" : c >= 0.4 ? "medium" : "low";

static int? YearFromDate(string? date)
{
    if (string.IsNullOrWhiteSpace(date)) return null;
    var m = System.Text.RegularExpressions.Regex.Match(date, @"(19|20)\d{2}");
    return m.Success ? int.Parse(m.Value) : null;
}

class ProcessProgress
{
    public int Total;
    public int Done;
    public volatile string Stage = "Preparing";
    public volatile bool Finished;
    public DateTime Started = DateTime.UtcNow;
}
