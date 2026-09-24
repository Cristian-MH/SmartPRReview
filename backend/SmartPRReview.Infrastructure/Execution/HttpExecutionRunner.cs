using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using SmartPRReview.Application.AI;
using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Infrastructure.Execution;

public sealed class RunnerOptions
{
    public string Url { get; set; } = "";
    public string ApiKey { get; set; } = "";
}
public sealed class HttpExecutionRunner(HttpClient http, IOptions<RunnerOptions> options, IPullRequestContextProvider source) : IExecutionRunner
{
    public async Task<ExecutionResult> ExecuteAsync(ReviewContext context, string? token, CancellationToken ct)
    {
        var config = options.Value;
        if (string.IsNullOrWhiteSpace(config.Url) || string.IsNullOrWhiteSpace(config.ApiKey)) return ExecutionResult.Skipped("Runner no configurado; compilación y pruebas no ejecutadas.");
        if (context.Snapshot.BaseRepository is not { } baseRepo || context.Snapshot.HeadRepository is not { } headRepo) return ExecutionResult.Skipped("No se pudo identificar el repositorio base/head.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(12));
            using var capabilityRequest = new HttpRequestMessage(HttpMethod.Get, config.Url.TrimEnd('/') + "/capabilities");
            capabilityRequest.Headers.Add("X-Runner-Key", config.ApiKey);
            using var capabilitiesResponse = await http.SendAsync(capabilityRequest, timeout.Token);
            if (!capabilitiesResponse.IsSuccessStatusCode) return ExecutionResult.Skipped("Runner no disponible o sin autorización.");
            var capabilities = await capabilitiesResponse.Content.ReadFromJsonAsync<RunnerCapabilities>(cancellationToken: timeout.Token);
            if (capabilities is null || !capabilities.AllowedRepositories.Contains(baseRepo, StringComparer.OrdinalIgnoreCase) ||
                !capabilities.AllowedRepositories.Contains(headRepo, StringComparer.OrdinalIgnoreCase) || !capabilities.ProfileRepositories.Contains(baseRepo, StringComparer.OrdinalIgnoreCase))
                return ExecutionResult.Skipped("Repositorio base/head o perfil no autorizado para ejecución.");
            var archive = await source.ArchiveAsync(context, token, timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, config.Url.TrimEnd('/') + "/execute");
            request.Headers.Add("X-Runner-Key", config.ApiKey);
            request.Content = JsonContent.Create(new ExecutionRequest(baseRepo, headRepo, context.Snapshot.HeadSha, archive));
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return ExecutionResult.Skipped($"Runner no pudo iniciar la ejecución (HTTP {(int)response.StatusCode}).");
            return await response.Content.ReadFromJsonAsync<ExecutionResult>(cancellationToken: timeout.Token) ?? ExecutionResult.Skipped("Runner devolvió una respuesta vacía.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ExecutionResult.Skipped("Tiempo de espera del runner agotado."); }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or AiRequestException)
        { return ExecutionResult.Skipped("No se pudo obtener evidencia del runner."); }
    }
}
