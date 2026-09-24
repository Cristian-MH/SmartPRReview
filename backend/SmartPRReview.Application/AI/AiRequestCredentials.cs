namespace SmartPRReview.Application.AI;

// Scoped to a single HTTP request. Never part of the cached review or model prompt.
public sealed class AiRequestCredentials
{
    private string? key;
    public void Set(string? value)
    {
        if (value?.Length > 4096 || value?.Any(char.IsControl) == true)
            throw new AiConfigurationException("La clave de IA tiene un formato inválido.", 400);
        key = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
    public string? Get() => key;
    public override string ToString() => "[REDACTED]";
}
