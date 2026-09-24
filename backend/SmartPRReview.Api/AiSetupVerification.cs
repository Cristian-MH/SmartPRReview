using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmartPRReview.Application.AI;
using SmartPRReview.Infrastructure.AI;

namespace SmartPRReview.Api;

public static class AiSetupVerification
{
    // Explicit operator command only. Synthetic input: no repository code or GitHub credentials.
    public static async Task VerifyAsync(IAiModelClient client, ProviderOptions config, ReviewLimits limits, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new AiRequestException($"Falta AI__Providers__{client.Provider}__ApiKey en el servidor.");
        var classifier = config.Models.SingleOrDefault(m => m.Id == config.ClassificationModel && m.Classification);
        var reviewer = config.Models.SingleOrDefault(m => m.Id == config.ReviewModel && m.Review && m.Tools);
        if (classifier is null || reviewer is null)
            throw new AiRequestException("Faltan modelos y capacidades para ambos roles.");
        // A small smoke test cannot establish a universal upper bound for an unknown tokenizer.
        if (classifier.TokenCounting != "Native" || reviewer.TokenCounting != "Native")
            throw new AiRequestException("La verificación automática requiere conteo nativo. DeepSeek necesita una evaluación independiente de su cota de tokens.");

        const string instructions = "This is a synthetic integration test. Follow the requested JSON schema. Respond in Spanish.";
        var classification = new AiRequest(classifier.Id, instructions,
            "Classify this documentation-only change: README.md adds installation instructions. Use category docs and evidence README.md.",
            ReviewSchemas.Classification, limits.ClassificationOutput, []);
        var result = await Call(classification, limits.ClassificationInput, classifier);
        ValidateFinal(result, classification.Schema);
        using (var json = JsonDocument.Parse(result.Json!))
            if (json.RootElement.GetProperty("category").GetString() != "docs")
                throw new AiRequestException("El clasificador no superó el caso de documentación.");

        var request = new AiRequest(reviewer.Id, instructions,
            "Before reviewing, call get_execution_result exactly once. After receiving the tool result, return a final review with no findings and a limitation explaining that tests were skipped. Do not call any other tool.",
            ReviewSchemas.Specialist, limits.SpecialistOutput, ReviewSchemas.Tools);
        var first = await Call(request, limits.SpecialistInput, reviewer);
        if (first.ToolCalls.Length != 1 || first.ToolCalls[0].Name != "get_execution_result")
            throw new AiRequestException("El revisor no superó la llamada a herramientas.");
        var tool = first.ToolCalls[0];
        ReviewSchemas.Validate(tool.Arguments, ReviewSchemas.Tools.Single(t => t.Name == tool.Name).Parameters);
        var final = await Call(request with { Tools = [], Previous = first,
            ToolResults = [new(tool.Id, tool.Name, "{\"status\":\"Skipped\",\"reason\":\"Synthetic setup test\",\"steps\":[]}")] }, limits.SpecialistInput, reviewer);
        ValidateFinal(final, request.Schema);

        async Task<AiResponse> Call(AiRequest input, int inputLimit, ModelDefinition model)
        {
            var count = await client.CountInputAsync(input, ct);
            if (count <= 0 || count > inputLimit || count + input.MaxOutputTokens > model.ContextTokens)
                throw new AiRequestException("El conteo de entrada no cumple los límites configurados.");
            var response = await client.CompleteAsync(input, ct);
            if (response.InputTokens is null or < 0 || response.OutputTokens is null or < 0 ||
                response.InputTokens > count || response.OutputTokens > input.MaxOutputTokens)
                throw new AiRequestException("El consumo reportado no cumple el presupuesto o no está disponible.");
            return response;
        }
    }

    private static void ValidateFinal(AiResponse response, JsonElement schema)
    {
        if (response.ToolCalls.Length != 0 || string.IsNullOrWhiteSpace(response.Json))
            throw new AiRequestException("El modelo no produjo una respuesta final JSON.");
        using var document = JsonDocument.Parse(response.Json);
        ReviewSchemas.Validate(document.RootElement, schema);
    }

    public static async Task<int> RunAsync(IServiceProvider services, string provider, string contentRoot, CancellationToken ct)
    {
        var options = services.GetRequiredService<IOptions<AiOptions>>().Value;
        var client = services.GetServices<IAiModelClient>().SingleOrDefault(c => c.Provider == provider);
        if (client is null || !options.Providers.TryGetValue(provider, out var config))
        {
            Console.Error.WriteLine("Proveedor no válido. Usa OpenAI, Gemini o DeepSeek.");
            return 1;
        }
        Console.WriteLine($"Verificando {provider} con datos sintéticos. Se realizarán hasta tres llamadas facturables, sin reintentos.");
        try
        {
            await VerifyAsync(client, config, options.Limits, ct);
            var path = Path.Combine(contentRoot, "appsettings.Ai.local.json");
            var root = File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!.AsObject() : new JsonObject();
            var ai = root["AI"] as JsonObject ?? new JsonObject(); root["AI"] = ai;
            var providers = ai["Providers"] as JsonObject ?? new JsonObject(); ai["Providers"] = providers;
            var settings = providers[provider] as JsonObject ?? new JsonObject(); providers[provider] = settings;
            settings["Enabled"] = true;
            settings["ClassificationModel"] = config.ClassificationModel;
            settings["ReviewModel"] = config.ReviewModel;
            settings["Models"] = JsonSerializer.SerializeToNode(config.Models.Select(m =>
                m.Id == config.ClassificationModel || m.Id == config.ReviewModel ? m with { Verified = true } : m));
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, root.ToJsonString(new() { WriteIndented = true }), ct);
            File.Move(temporary, path, overwrite: true);
            Console.WriteLine("Verificación completada. Configuración local actualizada sin copiar claves del entorno. Reinicia la API y recarga Vue.");
            return 0;
        }
        catch (AiRequestException ex) { Console.Error.WriteLine(ex.Message); }
        catch (OperationCanceledException) { Console.Error.WriteLine("Verificación cancelada; no se habilitaron modelos."); }
        catch (Exception) { Console.Error.WriteLine("No se pudo verificar o guardar la configuración. No se habilitaron nuevos modelos."); }
        return 1;
    }
}
