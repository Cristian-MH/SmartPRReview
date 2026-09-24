using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartPRReview.Application.AI;

namespace SmartPRReview.Infrastructure.AI;

public sealed class OpenAiModelClient(HttpClient http, IOptions<AiOptions> options, AiRequestCredentials? credentials = null) : JsonHttpModelClient(http, options, credentials)
{
    public override string Provider => "OpenAI";
    protected override string BaseUrl => "https://api.openai.com/v1/";
    private static List<object> Input(AiRequest r)
    {
        List<object> input = [new { role = "user", content = r.Input }];
        if (r.Previous is not null)
        {
            input.AddRange(r.Previous.Continuation.EnumerateArray().Select(x => (object)x));
            input.AddRange((r.ToolResults ?? []).Select(t => (object)new { type = "function_call_output", call_id = t.Id, output = t.Output }));
        }
        return input;
    }
    protected override object Payload(AiRequest r)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = r.Model, ["instructions"] = r.Instructions, ["input"] = Input(r), ["store"] = false,
            ["max_output_tokens"] = r.MaxOutputTokens,
            ["text"] = new { format = new { type = "json_schema", name = "review_result", strict = true, schema = r.Schema } },
            ["tools"] = r.Tools.Select(t => new { type = "function", name = t.Name, description = t.Description, parameters = t.Parameters, strict = true }),
        };
        var effort = Config.Models.Single(m => m.Id == r.Model).ReasoningEffort;
        // Also covers existing local configurations created before this setting existed.
        if (effort is null && (r.Model == "gpt-5-mini" || r.Model.StartsWith("gpt-5-mini-", StringComparison.Ordinal)))
            effort = "minimal";
        if (effort is not null) payload["reasoning"] = new { effort };
        return payload;
    }
    public override async Task<int> CountInputAsync(AiRequest r, CancellationToken ct)
    {
        if (Config.Models.Single(m => m.Id == r.Model).TokenCounting != "Native") return await base.CountInputAsync(r, ct);
        var body = JsonSerializer.SerializeToElement(Payload(r), Json);
        var fields = body.EnumerateObject().Where(p => p.Name is not ("store" or "max_output_tokens")).ToDictionary(p => p.Name, p => p.Value);
        var result = await PostAsync("responses/input_tokens", fields, ct);
        return checked((int)(Number(result, "input_tokens") ?? throw new AiRequestException("Conteo de tokens no disponible.")));
    }
    public override async Task<int?> CountTextAsync(string model, string text, CancellationToken ct)
    {
        if (Config.Models.Single(m => m.Id == model).TokenCounting != "Native") return null;
        var result = await PostAsync("responses/input_tokens", new { model, input = text }, ct);
        return checked((int?)Number(result, "input_tokens"));
    }
    public override async Task<AiResponse> CompleteAsync(AiRequest r, CancellationToken ct)
    {
        var result = await PostAsync("responses", Payload(r), ct);
        var output = result.GetProperty("output");
        List<AiToolCall> calls = [];
        string? text = null;
        if (String(result, "status") != "completed")
        {
            var reason = result.TryGetProperty("incomplete_details", out var details) && details.ValueKind == JsonValueKind.Object
                ? String(details, "reason") : null;
            var message = reason switch
            {
                "max_output_tokens" => $"OpenAI alcanzó el límite de salida de {r.MaxOutputTokens} tokens, que incluye razonamiento y respuesta. Revisa el esfuerzo de razonamiento o el presupuesto de salida del modelo.",
                "content_filter" => "OpenAI interrumpió la respuesta por un filtro de contenido.",
                _ => "OpenAI no completó la respuesta y no indicó una causa reconocida."
            };
            throw new AiRequestException(message) { PartialResponse = new(null, [], Number(result, "usage", "input_tokens"),
                Number(result, "usage", "input_tokens_details", "cached_tokens"), Number(result, "usage", "output_tokens"), default) };
        }
        foreach (var item in output.EnumerateArray())
        {
            if (String(item, "type") == "function_call")
                calls.Add(new(item.GetProperty("call_id").GetString()!, item.GetProperty("name").GetString()!, JsonDocument.Parse(item.GetProperty("arguments").GetString()!).RootElement.Clone()));
            if (String(item, "type") == "message") foreach (var content in item.GetProperty("content").EnumerateArray())
            {
                if (String(content, "type") == "refusal") throw new AiRequestException("OpenAI rechazó la solicitud.");
                if (String(content, "type") == "output_text") text = (text ?? "") + String(content, "text");
            }
        }
        return new(text, calls.ToArray(), Number(result, "usage", "input_tokens"), Number(result, "usage", "input_tokens_details", "cached_tokens"), Number(result, "usage", "output_tokens"), output.Clone());
    }
}
