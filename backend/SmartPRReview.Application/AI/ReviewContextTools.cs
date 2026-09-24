using System.Text;
using System.Text.Json;

namespace SmartPRReview.Application.AI;

public sealed class ReviewContextTools(IPullRequestContextProvider source)
{
    public async Task<AiToolResult> ExecuteAsync(AiToolCall call, ReviewContext context, Domain.Reviews.ExecutionResult execution,
        string? token, IAiModelClient client, string model, CancellationToken ct)
    {
        var definition = ReviewSchemas.Tools.SingleOrDefault(t => t.Name == call.Name) ?? throw new AiRequestException("Herramienta no permitida.");
        ReviewSchemas.Validate(call.Arguments, definition.Parameters);
        string result;
        if (call.Name == "read_file_range")
        {
            var path = call.Arguments.GetProperty("path").GetString()!;
            if (!SafePath(path)) throw new AiRequestException("Ruta de herramienta no permitida.");
            var file = await source.ReadAsync(context, path, call.Arguments.GetProperty("revision").GetString()!, token, ct);
            if (file is null) context.Limitations.Add($"El especialista solicitó contexto no disponible: {path}.");
            var start = call.Arguments.GetProperty("startLine").GetInt32();
            var count = call.Arguments.GetProperty("lineCount").GetInt32();
            result = file is null ? "Archivo no disponible dentro del contexto autorizado." :
                $"{file.Path} [{file.Revision}]\n" + string.Join('\n', file.Content.Split('\n').Skip(start - 1).Take(count).Select((line, i) => $"{start + i}: {line}"));
        }
        else if (call.Name == "search_snapshot")
        {
            var query = call.Arguments.GetProperty("query").GetString()!;
            if (string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new AiRequestException("Búsqueda inválida.");
            result = "Solo archivos recopilados; máximo 10 coincidencias.\n" + string.Join('\n', context.Sources.SelectMany(f => f.Content.Split('\n')
                .Select((line, i) => new { f.Path, f.Revision, Line = i + 1, Text = line })
                .Where(x => x.Text.Contains(query, StringComparison.Ordinal)).Take(10)).Take(10).Select(x => $"{x.Path} [{x.Revision}]:{x.Line}: {x.Text}"));
        }
        else result = JsonSerializer.Serialize(execution);
        var truncated = false;
        while (Encoding.UTF8.GetByteCount(result) > 16000 || (await client.CountTextAsync(model, result, ct) ?? Encoding.UTF8.GetByteCount(result)) > 3900)
        { result = result[..(result.Length / 2)]; truncated = true; }
        if (truncated) { result += "\n[Resultado truncado por presupuesto]"; context.Limitations.Add($"Resultado de {call.Name} truncado."); }
        return new(call.Id, call.Name, result);
    }
    public static bool SafePath(string path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 1024 && !path.StartsWith('/') &&
        !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl) && path.Split('/').All(p => p.Length > 0 && p is not ("." or ".."));
}
