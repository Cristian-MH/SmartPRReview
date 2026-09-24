using System.Net;
using System.Text;
using System.Text.Json;
using SmartPRReview.Application.Abstractions;
using SmartPRReview.Application.AI;
using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Infrastructure.GitHub;

public sealed class GitHubContextProvider(HttpClient http, IGitHubPullRequestClient client) : IPullRequestContextProvider
{
    private const int MaxFiles = 100, MaxFileBytes = 128 * 1024, MaxBytes = 2 * 1024 * 1024;
    private static readonly string[] Manifests = ["package.json", "tsconfig.json", "global.json", "Directory.Build.props", "Directory.Packages.props", "NuGet.Config"];
    public async Task<ReviewContext> CollectAsync(RepositoryReference repository, string? token, CancellationToken ct)
    {
        if (repository.Provider != RepositoryProvider.GitHub || repository.PullRequestNumber is null or <= 0)
            throw new AiRequestException("La revisión de IA requiere un pull request de GitHub válido.");
        var snapshot = await client.GetAsync(repository.Location, repository.PullRequestNumber.Value, ct, token);
        var context = new ReviewContext(snapshot);
        var repo = GitHubRepositoryLocation.Parse(repository.Location);
        var prefix = $"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}/pulls/{snapshot.Number}";
        for (var page = 1; ; page++)
        {
            var bytes = await GetBytesAsync($"{prefix}/commits?per_page=100&page={page}", token, 2 * 1024 * 1024, ct);
            using var doc = JsonDocument.Parse(bytes ?? throw new AiRequestException("No se pudieron recopilar los commits."));
            foreach (var commit in doc.RootElement.EnumerateArray()) context.Commits.Add(commit.GetProperty("commit").GetProperty("message").GetString() ?? "");
            if (doc.RootElement.GetArrayLength() < 100) break;
            if (page == 3) { context.Limitations.Add("Se recopilaron como máximo 300 mensajes de commit."); break; }
        }
        if (snapshot.Files.Count < snapshot.ChangedFileCount)
            context.Limitations.Add("GitHub no devolvió el inventario completo de archivos del PR.");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in snapshot.Files)
        {
            paths.Add(file.Path);
            var directory = file.Path.Contains('/') ? file.Path[..file.Path.LastIndexOf('/')] : "";
            while (true)
            {
                foreach (var manifest in Manifests) paths.Add(directory.Length == 0 ? manifest : directory + "/" + manifest);
                if (directory.Length == 0) break;
                directory = directory.Contains('/') ? directory[..directory.LastIndexOf('/')] : "";
            }
        }
        // Manifests first; bounded reads prevent an untrusted PR from making unlimited content requests.
        var ordered = paths.OrderByDescending(p => Manifests.Contains(p.Split('/').Last())).ThenBy(p => p, StringComparer.Ordinal).ToArray();
        foreach (var path in ordered.Take(MaxFiles))
        {
            var file = snapshot.Files.FirstOrDefault(f => f.Path == path);
            if (file?.Status != "removed") await ReadAsync(context, path, "head", token, ct);
            if (file is not null && file.Status != "added") await ReadAsync(context, file.PreviousPath ?? path, "base", token, ct);
        }
        if (ordered.Length > MaxFiles) context.Limitations.Add("Contexto limitado a 100 rutas de texto; otros archivos requieren revisión humana.");
        var check = await GetBytesAsync(prefix, token, 1024 * 1024, ct);
        using var current = JsonDocument.Parse(check ?? throw new AiRequestException("No se pudo verificar el commit del PR."));
        if (current.RootElement.GetProperty("head").GetProperty("sha").GetString() != snapshot.HeadSha ||
            current.RootElement.GetProperty("base").GetProperty("sha").GetString() != snapshot.BaseSha)
            throw new AiRequestException("El PR cambió durante la recopilación. Vuelve a solicitar la revisión.");
        return context;
    }
    public async Task<SourceFile?> ReadAsync(ReviewContext context, string path, string revision, string? token, CancellationToken ct)
    {
        if (!SafePath(path) || revision is not ("base" or "head")) throw new AiRequestException("Ruta o revisión de contexto inválida.");
        var existing = context.Sources.FirstOrDefault(s => s.Path == path && s.Revision == revision);
        if (existing is not null) return existing;
        if (context.Sources.Select(s => s.Path).Distinct().Count() >= MaxFiles && !context.Sources.Any(s => s.Path == path) || context.Bytes >= MaxBytes)
        { context.Limitations.Add($"Sin contexto para {path}: límite de recopilación."); return null; }
        var repository = revision == "head" ? context.Snapshot.HeadRepository : context.Snapshot.BaseRepository;
        if (repository is null) { context.Limitations.Add($"Repositorio {revision} no disponible para {path}."); return null; }
        var sha = revision == "head" ? context.Snapshot.HeadSha : context.Snapshot.BaseSha;
        var uri = $"repos/{RepositoryPath(repository)}/contents/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}?ref={Uri.EscapeDataString(sha)}";
        var bytes = await GetBytesAsync(uri, token, MaxFileBytes * 2, ct);
        if (bytes is null) { MissingChanged(context, path, revision); return null; }
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("type").GetString() != "file" ||
                root.GetProperty("size").GetInt64() > MaxFileBytes || root.GetProperty("encoding").GetString() != "base64")
            { MissingChanged(context, path, revision); return null; }
            var content = Convert.FromBase64String(root.GetProperty("content").GetString() ?? "");
            if (content.Contains((byte)0) || content.Length > MaxFileBytes || context.Bytes + content.Length > MaxBytes)
            { MissingChanged(context, path, revision); return null; }
            var text = new UTF8Encoding(false, true).GetString(content);
            var source = new SourceFile(path, revision, text);
            context.Sources.Add(source);
            context.Bytes += content.Length;
            return source;
        }
        catch (Exception e) when (e is JsonException or FormatException or DecoderFallbackException)
        { MissingChanged(context, path, revision); return null; }
    }
    private static void MissingChanged(ReviewContext c, string path, string revision)
    {
        if (c.Snapshot.Files.Any(f => f.Path == path || f.PreviousPath == path)) c.Limitations.Add($"Contenido {revision} no disponible, binario o demasiado grande: {path}.");
    }
    public async Task<byte[]> ArchiveAsync(ReviewContext context, string? token, CancellationToken ct) =>
        await GetBytesAsync($"repos/{RepositoryPath(context.Snapshot.HeadRepository ?? throw new AiRequestException("Repositorio head no disponible."))}/zipball/{Uri.EscapeDataString(context.Snapshot.HeadSha)}", token, 50 * 1024 * 1024, ct)
        ?? throw new AiRequestException("No se pudo descargar el snapshot para las pruebas.");

    public static bool SafePath(string path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 1024 &&
        !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') &&
        !path.Any(char.IsControl) && path.Split('/').All(p => p.Length > 0 && p is not ("." or ".."));
    private static string RepositoryPath(string name)
    {
        var parts = name.Split('/');
        if (parts.Length != 2 || !SafePath(name)) throw new AiRequestException("Identificador de repositorio inválido.");
        return string.Join('/', parts.Select(Uri.EscapeDataString));
    }
    private async Task<byte[]?> GetBytesAsync(string path, string? token, int maxBytes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new("Bearer", token.Trim());
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new AiRequestException($"GitHub no pudo proporcionar contexto (HTTP {(int)response.StatusCode}).");
        if (response.Content.Headers.ContentLength > maxBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
