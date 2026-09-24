using System.Security.Cryptography;
using System.Text;
using SmartPRReview.Domain.Reviews;
using SmartPRReview.Runner;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Runner.local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables().AddCommandLine(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 75 * 1024 * 1024);
builder.Services.Configure<ExecutionOptions>(builder.Configuration.GetSection("Runner"));
builder.Services.AddSingleton<DockerExecutor>();
var app = builder.Build();
app.Use(async (context, next) =>
{
    var expected = builder.Configuration["Runner:ApiKey"];
    var supplied = context.Request.Headers["X-Runner-Key"].ToString();
    if (string.IsNullOrWhiteSpace(expected) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
    { context.Response.StatusCode = 401; return; }
    await next(context);
});
app.MapGet("/capabilities", (DockerExecutor runner) => runner.Capabilities());
app.MapPost("/execute", async (ExecutionRequest request, DockerExecutor runner, CancellationToken ct) =>
{
    if (!runner.Allowed(request)) return Results.Problem(statusCode: 400, detail: "Repositorio o snapshot no autorizado.");
    if (!runner.TryAcquire()) return Results.Problem(statusCode: 429, detail: "Runner ocupado; no se encoló el trabajo.");
    try { return Results.Ok(await runner.ExecuteAsync(request, ct)); }
    finally { runner.Release(); }
});
app.Run();
