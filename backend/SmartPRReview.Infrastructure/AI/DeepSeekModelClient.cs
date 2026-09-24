using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartPRReview.Application.AI;

namespace SmartPRReview.Infrastructure.AI;

public sealed class DeepSeekModelClient(HttpClient http, IOptions<AiOptions> options, AiRequestCredentials? credentials = null) : JsonHttpModelClient(http, options, credentials)
{
    public override string Provider => "DeepSeek";
    protected override string BaseUrl => "https://api.deepseek.com/";
    protected override object Payload(AiRequest r)
    {
        List<object> messages = [new { role = "system", content = r.Instructions + "\nReturn JSON matching this schema: " + r.Schema.GetRawText() }, new { role = "user", content = r.Input }];
        if (r.Previous is not null)
        {
            messages.Add(r.Previous.Continuation);
            messages.AddRange((r.ToolResults ?? []).Select(t => (object)new { role = "tool", tool_call_id = t.Id, content = t.Output }));
        }
        return new { model = r.Model, messages, max_tokens = r.MaxOutputTokens,
            response_format = new { type = "json_object" },
            tools = r.Tools.Select(t => new { type = "function", function = new { name = t.Name, description = t.Description, parameters = t.Parameters } }).ToArray() };
    }
    public override async Task<AiResponse> CompleteAsync(AiRequest r, CancellationToken ct)
    {
        var result = await PostAsync("chat/completions", Payload(r), ct);
        var choice = result.GetProperty("choices")[0];
        if (String(choice, "finish_reason") is not ("stop" or "tool_calls")) throw new AiRequestException("DeepSeek no completó la respuesta.");
        var message = choice.GetProperty("message");
        if (String(message, "refusal") is { Length: > 0 }) throw new AiRequestException("DeepSeek rechazó la solicitud.");
        var calls = message.TryGetProperty("tool_calls", out var tools) ? tools.EnumerateArray().Select(t =>
            new AiToolCall(t.GetProperty("id").GetString()!, t.GetProperty("function").GetProperty("name").GetString()!,
                JsonDocument.Parse(t.GetProperty("function").GetProperty("arguments").GetString()!).RootElement.Clone())).ToArray() : [];
        // Preserve opaque reasoning/continuation fields only in request-local memory, never in reports.
        return new(String(message, "content"), calls, Number(result, "usage", "prompt_tokens"),
            Number(result, "usage", "prompt_cache_hit_tokens"), Number(result, "usage", "completion_tokens"), message.Clone());
    }
}
