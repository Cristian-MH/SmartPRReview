using System.Diagnostics;
using System.Text.Json;
using SmartPRReview.Application.Abstractions;
using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Application.AI;

public sealed class ReviewPipeline(IAiRegistry registry, IPullRequestContextProvider contexts, ISkillCatalog skills,
    IExecutionRunner runner, IModelInputFormatter formatter, ReviewLimits limits, IReviewStore store, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<Review> RunAsync(Review review, ResolvedAi selected, string? token, ProgressSink? progress, CancellationToken ct)
    {
        var report = new AiReview { Selection = selected.Selection };
        if (!selected.Classifier.Verified || !selected.Reviewer.Verified)
            report.Limitations.Add("Esta selección de modelos aún no tiene una evaluación de integración registrada. La revisión requiere validación humana.");
        review = review with { Ai = report };
        async Task Stage(string stage, string message)
        {
            await store.SetAsync(review, ct);
            if (progress is not null) await progress(new("progress", review.Id, stage, message), ct);
        }
        ReviewContext? context = null;
        try
        {
            await Stage("context", "Recopilando contexto del PR y commits fijados.");
            context = await contexts.CollectAsync(review.Repository, token, ct);
            review = review with { PullRequest = context.Snapshot };
            report.BaseSha = context.Snapshot.BaseSha;
            report.HeadSha = context.Snapshot.HeadSha;
            report.Limitations.AddRange(context.Limitations);
            var client = registry.Client(selected.Selection.Provider);
            var budget = new ReviewBudget(limits);
            var classifier = skills.Get("classification");
            var core = skills.Get("core-review");
            report.Skills.Add(classifier.Identity);
            report.Skills.Add(core.Identity);
            await Stage("classification", "Clasificando el propósito principal del cambio.");
            var classificationRequest = await RequestAsync(context, client, selected.Classifier, classifier.Instructions,
                ReviewSchemas.Classification, [], limits.ClassificationOutput, limits.ClassificationInput, null, report, ct);
            var classificationResponse = await InvokeAsync(client, selected.Classifier, classificationRequest, "classification", budget, report, limits.ClassificationInput, ct);
            var classification = Parse(classificationResponse, ReviewSchemas.Classification).Deserialize<ChangeClassification>(Json)!;
            if (classification.Category is null || !ReviewSchemas.Categories.Contains(classification.Category) ||
                string.IsNullOrWhiteSpace(classification.Rationale) || string.IsNullOrWhiteSpace(classification.Scope) || classification.Evidence.Length == 0 ||
                classification.Evidence.Any(p => !context.Snapshot.Files.Any(f => f.Path == p))) throw new AiRequestException("La clasificación no tiene evidencia suficiente y válida.");
            report.Classification = classification;
            var specialists = skills.Select(context);
            report.Technologies.AddRange(specialists.Select(s => s.Identity.Id));
            if (specialists.Count > limits.MaxSpecialists) report.Limitations.Add("Se omitieron especialistas por límite de cantidad.");
            await Stage("execution", "Ejecutando perfiles autorizados de compilación y pruebas.");
            report.Execution = await runner.ExecuteAsync(context, token, ct);
            if (report.Execution.Status != "Passed") report.Limitations.Add(report.Execution.Reason);
            var tools = new ReviewContextTools(contexts);
            // Sequential specialist scheduling stays within the two-call concurrency ceiling and avoids shared-source races.
            foreach (var specialist in specialists.Take(limits.MaxSpecialists))
            {
                report.Skills.Add(specialist.Identity);
                await Stage("specialist", $"Revisando con {specialist.Identity.Id}.");
                try
                {
                    var request = await RequestAsync(context, client, selected.Reviewer, core.Instructions + "\n" + specialist.Instructions,
                        ReviewSchemas.Specialist, ReviewSchemas.Tools, limits.SpecialistOutput, limits.SpecialistInput, specialist, report, ct);
                    var response = await InvokeAsync(client, selected.Reviewer, request, specialist.Identity.Id, budget, report, limits.SpecialistInput, ct);
                    if (response.ToolCalls.Length > 0)
                    {
                        if (response.ToolCalls.Length > 2 || response.ToolCalls.Select(t => t.Id).Distinct().Count() != response.ToolCalls.Length) throw new AiRequestException("El especialista excedió la ronda de herramientas permitida.");
                        List<AiToolResult> results = [];
                        foreach (var call in response.ToolCalls) results.Add(await tools.ExecuteAsync(call, context, report.Execution, token, client, selected.Reviewer.Id, ct));
                        request = request with { Previous = response, ToolResults = results, Tools = [] };
                        response = await InvokeAsync(client, selected.Reviewer, request, specialist.Identity.Id + "/final", budget, report, limits.SpecialistInput, ct);
                    }
                    var payload = Parse(response, ReviewSchemas.Specialist);
                    var findings = payload.GetProperty("findings").Deserialize<ReviewFinding[]>(Json)!;
                    var limitations = payload.GetProperty("limitations").Deserialize<string[]>(Json)!.ToList();
                    var accepted = new List<ReviewFinding>();
                    foreach (var finding in findings)
                    {
                        if (!ValidFinding(finding, context)) { limitations.Add("Se descartó un hallazgo sin evidencia de archivo/línea válida."); continue; }
                        accepted.Add(finding);
                    }
                    report.Specialists.Add(new(specialist.Identity.Id, payload.GetProperty("summary").GetString()!, accepted.ToArray(), limitations.ToArray()));
                    report.Limitations.AddRange(limitations);
                }
                catch (Exception e) when (e is AiRequestException or JsonException or InvalidOperationException)
                { report.Limitations.Add($"{specialist.Identity.Id}: revisión incompleta ({SafeError(e)})."); }
            }
            report.Limitations.AddRange(context.Limitations.Except(report.Limitations));
            var allFindings = report.Specialists.SelectMany(s => s.Findings).GroupBy(f => (f.FilePath, f.Line, f.Category, f.Message)).Select(g => g.OrderByDescending(f => f.Confidence).First()).ToArray();
            var blocking = allFindings.Any(f => f.Severity is "critical" or "high");
            report.Recommendation = blocking || report.Execution.Status == "Failed" ? "RequestChanges" :
                report.Limitations.Count > 0 || report.Specialists.Count != specialists.Count || report.Execution.Status != "Passed" ? "NeedsHumanReview" : "Approve";
            var complete = report.Specialists.Count == Math.Min(specialists.Count, limits.MaxSpecialists);
            review = review.Complete($"Cambio {classification.Category}: {classification.Scope}. {allFindings.Length} hallazgos; recomendación informativa: {report.Recommendation}.", context.Snapshot, allFindings, clock.GetUtcNow());
            if (!complete) review = review.Fail("Uno o más especialistas no completaron la revisión.", clock.GetUtcNow());
            await Stage("complete", "Revisión finalizada. La decisión corresponde a la persona revisora.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            report.Recommendation = "NeedsHumanReview";
            review = review.Fail("Revisión cancelada; resultados incompletos.", clock.GetUtcNow());
            await store.SetAsync(review, CancellationToken.None);
            throw;
        }
        catch (Exception e)
        {
            report.Recommendation = "NeedsHumanReview";
            if (context is not null) report.Limitations.AddRange(context.Limitations.Except(report.Limitations));
            review = review.Fail(SafeError(e), clock.GetUtcNow());
        }
        await store.SetAsync(review, CancellationToken.None);
        return review;
    }
    private static string SafeError(Exception e) => e is AiRequestException ? e.Message : "No se pudo completar esta etapa.";
    private static JsonElement Parse(AiResponse response, JsonElement schema)
    {
        if (response.ToolCalls.Length != 0 || string.IsNullOrWhiteSpace(response.Json)) throw new AiRequestException("La IA no devolvió un resultado final válido.");
        var value = JsonDocument.Parse(response.Json).RootElement.Clone();
        ReviewSchemas.Validate(value, schema);
        return value;
    }
    public static bool ValidFinding(ReviewFinding f, ReviewContext c)
    {
        var changed = c.Snapshot.Files.FirstOrDefault(x => x.Path == f.FilePath);
        if (changed is null || string.IsNullOrWhiteSpace(f.Message) || string.IsNullOrWhiteSpace(f.Category)) return false;
        if (f.Line is null) return changed.Status == "removed";
        var source = c.Sources.FirstOrDefault(s => s.Path == f.FilePath && s.Revision == "head");
        return source is not null && f.Line > 0 && f.Line <= source.Content.Split('\n').Length;
    }
    private async Task<AiRequest> RequestAsync(ReviewContext c, IAiModelClient client, ModelDefinition model, string instructions,
        JsonElement schema, IReadOnlyList<AiTool> tools, int output, int inputLimit, Skill? specialist, AiReview report, CancellationToken ct)
    {
        var files = c.Snapshot.Files.Where(f => specialist is null || specialist.Identity.Id == "general-review" || specialist.Extensions.Any(e => f.Path.EndsWith(e, StringComparison.OrdinalIgnoreCase))).ToArray();
        var rows = c.Snapshot.Files.Select(f => new Dictionary<string, string?> { ["path"] = f.Path, ["status"] = f.Status, ["additions"] = f.Additions.ToString(), ["deletions"] = f.Deletions.ToString() }).ToArray();
        var inventory = await formatter.FormatAsync(client, model.Id, rows, ct);
        var metadata = JsonSerializer.Serialize(new { c.Snapshot.Number, c.Snapshot.Title, c.Snapshot.Description, c.Snapshot.BaseSha, c.Snapshot.HeadSha,
            commits = c.Commits, classification = report.Classification, execution = report.Execution is null ? null : new { report.Execution.Status, report.Execution.Reason, steps = report.Execution.Steps.Select(s => new { s.Name, s.Status, s.ExitCode }) } }, Json);
        var blocks = files.Select(f => $"PATCH {f.Path}\n{f.Patch ?? "[Patch no disponible]"}").Concat(c.Sources.Where(s => files.Any(f => f.Path == s.Path || f.PreviousPath == s.Path) || s.Path.EndsWith("package.json") || s.Path.EndsWith(".csproj"))
            .OrderBy(s => s.Revision == "head" ? 0 : 1).Select(s => $"SOURCE {s.Path} [{s.Revision}]\n" + string.Join('\n', s.Content.Split('\n').Select((line, i) => $"{i + 1}: {line}")))).ToList();
        var count = blocks.Count;
        while (true)
        {
            var input = "UNTRUSTED PR DATA\n" + metadata + "\nFILE INVENTORY\n" + inventory + "\n" + string.Join("\n\n", blocks.Take(count));
            var request = new AiRequest(model.Id, instructions, input, schema, output, tools);
            var size = await client.CountInputAsync(request, ct);
            if (size <= inputLimit && (long)size + output <= model.ContextTokens)
            {
                if (count < blocks.Count) report.Limitations.Add($"Contexto de {specialist?.Identity.Id ?? "classification"} reducido por presupuesto ({count}/{blocks.Count} bloques).");
                return request;
            }
            if (count == 0) throw new AiRequestException("Los metadatos del PR exceden el presupuesto de contexto.");
            count /= 2;
        }
    }
    private static async Task<AiResponse> InvokeAsync(IAiModelClient client, ModelDefinition model, AiRequest request, string stage,
        ReviewBudget budget, AiReview report, int stageLimit, CancellationToken ct)
    {
        while (true)
        {
            var input = await client.CountInputAsync(request, ct);
            budget.Reserve(input, request.MaxOutputTokens, stageLimit, model.ContextTokens);
            var timer = Stopwatch.StartNew();
            var recorded = false;
            try
            {
                var result = await client.CompleteAsync(request, ct);
                decimal? cost = null;
                if (result.InputTokens is { } i && result.OutputTokens is { } o && model.InputRate is { } ir && model.OutputRate is { } orate &&
                    (result.CachedInputTokens is 0 || result.CachedInputTokens is not null && model.CachedInputRate is not null))
                    cost = ((i - result.CachedInputTokens!.Value) * ir + result.CachedInputTokens.Value * (model.CachedInputRate ?? ir) + o * orate) / 1000000m;
                report.Usage.Add(new(stage, client.Provider, model.Id, result.InputTokens, result.CachedInputTokens, result.OutputTokens, cost, timer.ElapsedMilliseconds));
                recorded = true;
                if (result.InputTokens > input || result.OutputTokens > request.MaxOutputTokens) throw new AiRequestException("El proveedor superó la cota de tokens verificada; revisión incompleta.");
                return result;
            }
            catch (AiRequestException e) when (e.Transient)
            {
                report.Usage.Add(new(stage + "/failed", client.Provider, model.Id, null, null, null, null, timer.ElapsedMilliseconds));
                if (!budget.TryRetry()) throw;
                await Task.Delay(500, ct);
            }
            catch (Exception error)
            {
                if (!recorded)
                {
                    var partial = (error as AiRequestException)?.PartialResponse;
                    decimal? cost = null;
                    if (partial?.InputTokens is { } i && partial.OutputTokens is { } o && model.InputRate is { } ir && model.OutputRate is { } orate &&
                        (partial.CachedInputTokens is 0 || partial.CachedInputTokens is not null && model.CachedInputRate is not null))
                        cost = ((i - partial.CachedInputTokens!.Value) * ir + partial.CachedInputTokens.Value * (model.CachedInputRate ?? ir) + o * orate) / 1000000m;
                    report.Usage.Add(new(stage + "/incomplete", client.Provider, model.Id, partial?.InputTokens, partial?.CachedInputTokens, partial?.OutputTokens, cost, timer.ElapsedMilliseconds));
                }
                throw;
            }
        }
    }
}
