using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartPRReview.Application.AI;

namespace SmartPRReview.Infrastructure.AI;

public abstract class JsonHttpModelClient(HttpClient http, IOptions<AiOptions> options, AiRequestCredentials? credentials = null) : IAiModelClient
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public abstract string Provider { get; }
    protected ProviderOptions Config => options.Value.Providers[Provider];
    protected abstract string BaseUrl { get; }
    protected abstract object Payload(AiRequest request);
    public abstract Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken);
    public virtual Task<int?> CountTextAsync(string model, string text, CancellationToken cancellationToken) => Task.FromResult<int?>(null);
    public virtual Task<int> CountInputAsync(AiRequest request, CancellationToken cancellationToken)
    {
        // Only enabled after the operator has verified this bound for the configured text-only model.
        // Counting the complete wire payload covers schemas, continuation state and tool definitions.
        var count = checked(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(Payload(request), Json)) + 4096);
        return Task.FromResult(count);
    }
    protected async Task<JsonElement> PostAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(Config.TimeoutSeconds, 1, 600)));
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path);
        var apiKey = credentials?.Get() ?? Config.ApiKey;
        if (Provider == "Gemini") request.Headers.Add("x-goog-api-key", apiKey);
        else request.Headers.Authorization = new("Bearer", apiKey);
        request.Content = JsonContent.Create(body, options: Json);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new AiRequestException($"{Provider} devolvió HTTP {(int)response.StatusCode}.",
                    response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(bytes, timeout.Token)) > 0)
            {
                if (buffer.Length + read > 2 * 1024 * 1024) throw new AiRequestException("Respuesta de IA demasiado grande.");
                buffer.Write(bytes, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new AiRequestException($"Tiempo de espera agotado en {Provider}.", true); }
        catch (HttpRequestException) { throw new AiRequestException($"No se pudo conectar con {Provider}.", true); }
        catch (JsonException) { throw new AiRequestException($"Respuesta inválida de {Provider}."); }
    }
    protected static long? Number(JsonElement element, params string[] path)
    {
        foreach (var part in path) if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part, out element)) return null;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var n) ? n : null;
    }
    protected static string? String(JsonElement value, string property) => value.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
