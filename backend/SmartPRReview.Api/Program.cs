using System.Text.Json.Serialization;
using SmartPRReview.Application.Reviews;
using SmartPRReview.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Ai.local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables().AddCommandLine(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ReviewService>();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "SmartPRReview API",
        Version = "v1",
        Description = "API for submitting repositories and retrieving AI-assisted review results."
    });
});

var app = builder.Build();

if (builder.Configuration["verify-ai"] is { } verificationProvider)
{
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(6));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    using var verificationScope = app.Services.CreateScope();
    Environment.ExitCode = await SmartPRReview.Api.AiSetupVerification.RunAsync(
        verificationScope.ServiceProvider, verificationProvider, builder.Environment.ContentRootPath, cancellation.Token);
    await app.DisposeAsync();
    return;
}

app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "SmartPRReview API v1");
    options.DocumentTitle = "SmartPRReview API";
});

app.MapControllers();

app.Run();

public partial class Program;
