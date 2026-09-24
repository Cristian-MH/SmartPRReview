using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartPRReview.Application.AI;

namespace SmartPRReview.Infrastructure.AI;

public sealed class GeminiModelClient(HttpClient http, IOptions<AiOptions> options, AiRequestCredentials? credentials = null) : JsonHttpModelClient(http, options, credentials)
{
    public override string Provider => "Gemini";
    protected override string BaseUrl => "https://generativelanguage.googleapis.com/v1beta/";
    protected override object Payload(AiRequest r)
    {
        List<object> contents = [new { role = "user", parts = new[] { new { text = r.Input } } }];
        if (r.Previous is not null)
        {
            contents.Add(r.Previous.Continuation);
            contents.Add(new { role = "user", parts = (r.ToolResults ?? []).Select(t => new { functionResponse = new { name = t.Name, response = new { result = t.Output } } }).ToArray() });
        }
        return new { systemInstruction = new { parts = new[] { new { text = r.Instructions } } }, contents,
            generationConfig = new { maxOutputTokens = r.MaxOutputTokens, responseMimeType = "application/json", responseJsonSchema = r.Schema },
            tools = r.Tools.Count == 0 ? [] : new[] { new { functionDeclarations = r.Tools.Select(t => new { name = t.Name, description = t.Description, parametersJsonSchema = t.Parameters }).ToArray() } } };
    }
    public override async Task<int> CountInputAsync(AiRequest r, CancellationToken ct)
    {
        if (Config.Models.Single(m => m.Id == r.Model).TokenCounting != "Native") return await base.CountInputAsync(r, ct);
        var payload = JsonSerializer.SerializeToElement(Payload(r), Json).EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value);
        payload["model"] = "models/" + r.Model;
        var response = await PostAsync($"models/{Uri.EscapeDataString(r.Model)}:countTokens", new { generateContentRequest = payload }, ct);
        return checked((int)(Number(response, "totalTokens") ?? throw new AiRequestException("Conteo de tokens no disponible.")));
    }
    public override async Task<int?> CountTextAsync(string model, string text, CancellationToken ct)
    {
        if (Config.Models.Single(m => m.Id == model).TokenCounting != "Native") return null;
        var response = await PostAsync($"models/{Uri.EscapeDataString(model)}:countTokens", new { contents = new[] { new { role = "user", parts = new[] { new { text } } } } }, ct);
        return checked((int?)Number(response, "totalTokens"));
    }
    public override async Task<AiResponse> CompleteAsync(AiRequest r, CancellationToken ct)
    {
        var result = await PostAsync($"models/{Uri.EscapeDataString(r.Model)}:generateContent", Payload(r), ct);
        if (!result.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0) throw new AiRequestException("Gemini rechazó la solicitud.");
        var candidate = candidates[0];
        if (String(candidate, "finishReason") != "STOP") throw new AiRequestException("Gemini no completó la respuesta.");
        var content = candidate.GetProperty("content");
        List<AiToolCall> calls = [];
        string? text = null;
        foreach (var part in content.GetProperty("parts").EnumerateArray())
        {
            if (part.TryGetProperty("functionCall", out var call)) calls.Add(new(Guid.NewGuid().ToString("N"), call.GetProperty("name").GetString()!, call.GetProperty("args").Clone()));
            else if (!part.TryGetProperty("thought", out var thought) || !thought.GetBoolean()) text = (text ?? "") + String(part, "text");
        }
        var output = Number(result, "usageMetadata", "candidatesTokenCount");
        var reasoning = Number(result, "usageMetadata", "thoughtsTokenCount");
        return new(text, calls.ToArray(), Number(result, "usageMetadata", "promptTokenCount"), Number(result, "usageMetadata", "cachedContentTokenCount"),
            output.HasValue ? output + (reasoning ?? 0) : null, content.Clone());
    }
}
