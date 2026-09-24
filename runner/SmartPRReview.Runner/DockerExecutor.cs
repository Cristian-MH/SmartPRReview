using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SmartPRReview.Domain.Reviews;

namespace SmartPRReview.Runner;

public sealed class ExecutionOptions
{
    public string[] AllowedRepositories { get; set; } = [];
    public ExecutionProfile[] Profiles { get; set; } = [];
    public string RestoreNetwork { get; set; } = "";
    public string RegistryProxy { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 600;
}
public sealed class ExecutionProfile
{
    public string Repository { get; set; } = "";
    public string Image { get; set; } = "";
    public string Directory { get; set; } = ".";
    public string[] Restore { get; set; } = [];
    public string[] Build { get; set; } = [];
    public string[] Test { get; set; } = [];
}
public sealed class DockerExecutor(IOptions<ExecutionOptions> options, ILogger<DockerExecutor> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public bool TryAcquire() => gate.Wait(0);
    public void Release() => gate.Release();
    public RunnerCapabilities Capabilities() => new(options.Value.AllowedRepositories, options.Value.Profiles.Select(p => p.Repository).ToArray());
    public bool Allowed(ExecutionRequest r) => options.Value.AllowedRepositories.Contains(r.BaseRepository, StringComparer.OrdinalIgnoreCase) &&
        options.Value.AllowedRepositories.Contains(r.HeadRepository, StringComparer.OrdinalIgnoreCase) &&
        options.Value.Profiles.Any(p => p.Repository.Equals(r.BaseRepository, StringComparison.OrdinalIgnoreCase)) &&
        Regex.IsMatch(r.HeadSha, "\\A[a-fA-F0-9]{40}\\z") && r.Archive.Length is > 0 and <= 50 * 1024 * 1024;

    public async Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.TimeoutSeconds, 1, 600)));
        var ct = timeout.Token;
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "smartpr-runner", Guid.NewGuid().ToString("N")));
        var containers = new List<string>();
        List<ExecutionStep> steps = [];
        try
        {
            Directory.CreateDirectory(root);
            ExtractSafe(request.Archive, root);
            var config = options.Value;
            if (string.IsNullOrWhiteSpace(config.RestoreNetwork) || !Uri.TryCreate(config.RegistryProxy, UriKind.Absolute, out var proxy) || proxy.Scheme != "http" || proxy.UserInfo.Length > 0)
                return ExecutionResult.Skipped("Se requiere una red interna y proxy de registros sin credenciales.");
            var network = await Docker(["network", "inspect", "--format", "{{.Internal}}", config.RestoreNetwork], ct);
            if (network.Code != 0 || network.Log.Trim() != "true") return ExecutionResult.Skipped("La red de restauración debe ser interna y aislada.");
            foreach (var profile in config.Profiles.Where(p => p.Repository.Equals(request.BaseRepository, StringComparison.OrdinalIgnoreCase)))
            {
                if (!profile.Image.Contains("@sha256:", StringComparison.Ordinal) || profile.Restore.Length == 0 || profile.Build.Length == 0 || profile.Test.Length == 0 || !SafeDirectory(profile.Directory))
                    return new("Unavailable", "Perfil incompleto: se requiere imagen fijada por digest y comandos de build/unit tests.", steps.ToArray());
                if (!Directory.Exists(Path.Combine(root, profile.Directory))) return new("Unavailable", "Directorio del perfil no encontrado en el snapshot.", steps.ToArray());
                var name = "smartpr-" + Guid.NewGuid().ToString("N");
                containers.Add(name);
                await Require(["create", "--name", name, "--network", "none", "--cpus", "2", "--memory", "4g", "--memory-swap", "4g", "--pids-limit", "256",
                    "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--read-only", "--user", "10001:10001",
                    "--tmpfs", "/work:rw,exec,size=1073741824,uid=10001,gid=10001", "--tmpfs", "/tmp:rw,exec,size=1073741824,uid=10001,gid=10001",
                    "--env", "HOME=/tmp/home", "--env", "CI=true", "--env", "DOTNET_CLI_HOME=/tmp/home", "--env", "NUGET_PACKAGES=/work/.nuget",
                    "--entrypoint", "sleep", profile.Image, "infinity"], ct);
                await Require(["start", name], ct);
                await Require(["cp", root, name + ":/work/source"], ct);
                await Require(["exec", "--user", "0", name, "chmod", "-R", "a+rwX", "/work/source"], ct);
                await Require(["network", "connect", config.RestoreNetwork, name], ct);
                var work = "/work/source" + (profile.Directory == "." ? "" : "/" + profile.Directory);
                var restoreArgs = new List<string> { "exec", "--workdir", work, "--env", "HTTP_PROXY=" + config.RegistryProxy, "--env", "HTTPS_PROXY=" + config.RegistryProxy,
                    "--env", "http_proxy=" + config.RegistryProxy, "--env", "https_proxy=" + config.RegistryProxy, name };
                restoreArgs.AddRange(profile.Restore);
                var restored = await Docker(restoreArgs, ct);
                steps.Add(new(profile.Directory + "/restore", restored.Code == 0 ? "Passed" : "Unavailable", restored.Code, restored.Log));
                await Require(["network", "disconnect", config.RestoreNetwork, name], ct);
                if (restored.Code != 0) return new("Unavailable", "No se pudieron restaurar dependencias; no demuestra un defecto del cambio.", steps.ToArray());
                foreach (var (stage, command) in new[] { ("build", profile.Build), ("unit-tests", profile.Test) })
                {
                    var args = new List<string> { "exec", "--workdir", work, name }; args.AddRange(command);
                    var result = await Docker(args, ct);
                    steps.Add(new(profile.Directory + "/" + stage, result.Code == 0 ? "Passed" : "Failed", result.Code, result.Log));
                    if (result.Code != 0) return new("Failed", "Falló build/unit tests en head; no se comparó con baseline.", steps.ToArray());
                }
            }
            return new("Passed", "Perfiles configurados completados; E2E y baseline no ejecutados.", steps.ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new("Unavailable", "Tiempo máximo de ejecución agotado.", steps.ToArray()); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { logger.LogWarning("Runner failed: {Type}", e.GetType().Name); return new("Unavailable", "No se pudo ejecutar el snapshot en el entorno aislado.", steps.ToArray()); }
        finally
        {
            foreach (var name in containers)
            {
                try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await Docker(["rm", "-f", name], cleanup.Token); }
                catch (Exception e) { logger.LogError("Container cleanup failed for {Container}: {Type}", name, e.GetType().Name); }
            }
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "smartpr-runner")) + Path.DirectorySeparatorChar;
            if (root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    public static bool SafeDirectory(string path) => path == "." || (!string.IsNullOrWhiteSpace(path) && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') && path.Split('/').All(p => p.Length > 0 && p is not ("." or "..")));
    public static void ExtractSafe(byte[] bytes, string root)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > 20000) throw new InvalidDataException("Archive entry limit.");
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            total += entry.Length;
            if (total > 512 * 1024 * 1024 || entry.Length > 64 * 1024 * 1024 || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                throw new InvalidDataException("Archive limit or symlink.");
            var parts = entry.FullName.Split('/');
            if (parts.Any(p => p is "." or "..") || entry.FullName.Contains('\\') || entry.FullName.Contains(':') || entry.FullName.StartsWith('/')) throw new InvalidDataException("Unsafe archive path.");
            // GitHub zipball wraps the repository in one top-level directory.
            var relative = string.Join('/', parts.Skip(1));
            if (relative.Length == 0) continue;
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive path escapes workspace.");
            if (entry.FullName.EndsWith('/')) Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew);
                input.CopyTo(output);
            }
        }
    }
    private static async Task Require(IEnumerable<string> arguments, CancellationToken ct)
    {
        var result = await Docker(arguments, ct);
        if (result.Code != 0) throw new InvalidOperationException("Docker setup unavailable.");
    }
    private static async Task<(int Code, string Log)> Docker(IEnumerable<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker not available.");
        var log = new StringBuilder();
        async Task Drain(StreamReader reader)
        {
            var buffer = new char[2048];
            int read;
            while ((read = await reader.ReadAsync(buffer, ct)) > 0) lock (log) { if (log.Length < 16000) log.Append(buffer, 0, Math.Min(read, 16000 - log.Length)); }
        }
        var drains = Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError));
        try { await Task.WhenAll(process.WaitForExitAsync(ct), drains); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        return (process.ExitCode, log.ToString());
    }
}
