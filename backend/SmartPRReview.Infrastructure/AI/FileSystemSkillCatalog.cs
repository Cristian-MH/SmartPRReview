using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartPRReview.Application.AI;
using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Infrastructure.AI;

public sealed class FileSystemSkillCatalog : ISkillCatalog
{
    private readonly Dictionary<string, Skill> skills = new(StringComparer.Ordinal);
    public FileSystemSkillCatalog() : this(Path.Combine(AppContext.BaseDirectory, "skills")) { }
    public FileSystemSkillCatalog(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var metadata = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(directory, "manifest.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var instructions = File.ReadAllText(Path.Combine(directory, "SKILL.md"));
            foreach (var reference in metadata.References)
            {
                var resolved = Path.GetFullPath(Path.Combine(directory, reference));
                if (!resolved.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("Invalid skill reference.");
                instructions += "\n" + File.ReadAllText(resolved);
            }
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata) + instructions))).ToLowerInvariant();
            skills.Add(metadata.Id, new(new(metadata.Id, metadata.Version, hash), instructions, metadata.Extensions, metadata.Markers, metadata.References));
        }
    }
    public Skill Get(string id) => skills[id];
    public IReadOnlyList<Skill> Select(ReviewContext context)
    {
        var paths = context.Snapshot.Files.Select(f => f.Path).ToArray();
        var manifests = string.Join('\n', context.Sources.Where(s => s.Revision == "head" && s.Path.EndsWith("package.json")).Select(s => s.Content));
        List<Skill> selected = [];
        foreach (var skill in skills.Values.Where(s => s.Identity.Id is not ("classification" or "core-review" or "general-review")))
        {
            if (!paths.Any(p => skill.Extensions.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)))) continue;
            if (skill.Identity.Id == "vue-typescript" && !paths.Any(p => p.EndsWith(".vue")) && !skill.Markers.Any(manifests.Contains)) continue;
            selected.Add(skill);
        }
        if (selected.Count == 0 || paths.Any(p => !selected.Any(s => s.Extensions.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase))))) selected.Add(Get("general-review"));
        return selected.OrderBy(s => s.Identity.Id, StringComparer.Ordinal).ToArray();
    }
    private sealed record Manifest(string Id, string Version, string[] Extensions, string[] Markers, string[] References);
}
