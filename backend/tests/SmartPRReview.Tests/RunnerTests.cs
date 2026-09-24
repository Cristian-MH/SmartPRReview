using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartPRReview.Domain.Reviews;
using SmartPRReview.Runner;
using Xunit;

namespace SmartPRReview.Tests;

public sealed class RunnerTests
{
    [Fact]
    public void ForksNeedTheirOwnAllowlistEntryAndNoJobsQueue()
    {
        var runner = new DockerExecutor(Options.Create(new ExecutionOptions { AllowedRepositories = ["o/r"], Profiles = [new() { Repository = "o/r" }] }), NullLogger<DockerExecutor>.Instance);
        Assert.True(runner.Allowed(new("o/r", "o/r", new string('a', 40), [1])));
        Assert.False(runner.Allowed(new("o/r", "fork/r", new string('a', 40), [1])));
        Assert.True(runner.TryAcquire()); Assert.False(runner.TryAcquire()); runner.Release(); Assert.True(runner.TryAcquire()); runner.Release();
    }
    [Theory]
    [InlineData("root/../escape")][InlineData("/etc/passwd")][InlineData("root/C:/escape")][InlineData("root/dir\\escape")]
    public void ArchiveTraversalIsRejected(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "smartpr-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Assert.Throws<InvalidDataException>(() => DockerExecutor.ExtractSafe(Archive(name), root)); }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void GitHubArchiveWrapperIsRemoved()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartpr-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { DockerExecutor.ExtractSafe(Archive("owner-repo-sha/src/a.txt"), root); Assert.Equal("content", File.ReadAllText(Path.Combine(root, "src/a.txt"))); }
        finally { Directory.Delete(root, true); }
    }
    private static byte[] Archive(string path)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true)) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), Encoding.UTF8); writer.Write("content"); }
        return memory.ToArray();
    }
}
