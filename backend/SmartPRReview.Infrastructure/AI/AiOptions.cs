using Microsoft.Extensions.Options;
using SmartPRReview.Application.AI;
using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Infrastructure.AI;

public sealed class AiOptions
{
    public Dictionary<string, ProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ReviewLimits Limits { get; set; } = new();
}
public sealed class ProviderOptions
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = "";
    public string ClassificationModel { get; set; } = "";
    public string ReviewModel { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 120;
    public ModelDefinition[] Models { get; set; } = [];
}
public sealed class AiRegistry(IOptions<AiOptions> options, IEnumerable<IAiModelClient> clients, AiRequestCredentials? credentials = null) : IAiRegistry
{
    private static bool Eligible(ModelDefinition m) => m.StructuredOutput && m.ContextTokens > 0 &&
        m.InputRate is not < 0 && m.CachedInputRate is not < 0 && m.OutputRate is not < 0 &&
        (m.TokenCounting == "Native" || m.TokenCounting == "VerifiedUtf8UpperBound" && m.Verified);

    public AiCatalog Catalog() => new("OpenAI", options.Value.Providers
        .Where(p => clients.Any(c => c.Provider == p.Key))
        .Select(p => new ProviderCatalog(p.Key, p.Value.ClassificationModel, p.Value.ReviewModel,
            p.Value.Models.Where(m => Eligible(m) && (p.Key != "DeepSeek" || m.TokenCounting == "VerifiedUtf8UpperBound")).ToArray(),
            p.Value.Enabled && !string.IsNullOrWhiteSpace(p.Value.ApiKey)))
        .Where(p => p.Models.Any(m => m.Id == p.ClassificationModel && m.Classification) &&
            p.Models.Any(m => m.Id == p.ReviewModel && m.Review && m.Tools)).ToArray());

    public ResolvedAi Resolve(AiSelection? selection)
    {
        var provider = selection?.Provider ?? "OpenAI";
        if (!clients.Any(c => c.Provider == provider))
            throw new AiConfigurationException("Proveedor de IA no válido.", 400);
        if (!options.Value.Providers.TryGetValue(provider, out var config))
            throw new AiConfigurationException($"El proveedor {provider} no está configurado.", 503);
        var requestKey = !string.IsNullOrWhiteSpace(credentials?.Get());
        if (!requestKey && (!config.Enabled || string.IsNullOrWhiteSpace(config.ApiKey)))
            throw new AiConfigurationException("Envía aiApiKey con la clave del proveedor de IA seleccionado.", 400);
        var classifier = selection?.ClassificationModel ?? config.ClassificationModel;
        var reviewer = selection?.ReviewModel ?? config.ReviewModel;
        if (string.IsNullOrWhiteSpace(classifier) || string.IsNullOrWhiteSpace(reviewer))
            throw new AiConfigurationException("Faltan modelos predeterminados.", 503);
        var c = config.Models.SingleOrDefault(m => m.Id == classifier && m.Classification && Eligible(m) && (m.Verified || requestKey));
        var r = config.Models.SingleOrDefault(m => m.Id == reviewer && m.Review && m.Tools && Eligible(m) && (m.Verified || requestKey));
        if (c is null || r is null)
            throw new AiConfigurationException("Modelo no permitido o capacidades no verificadas.", 400);
        if (provider == "DeepSeek" && (c.TokenCounting == "Native" || r.TokenCounting == "Native"))
            throw new AiConfigurationException("DeepSeek requiere una cota UTF-8 verificada en la configuración.", 503);
        return new(new(provider, classifier, reviewer), c, r);
    }
    public IAiModelClient Client(string provider) => clients.Single(c => c.Provider == provider);
}
