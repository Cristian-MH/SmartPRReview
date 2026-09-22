using System.Text.Json.Serialization;
using SmartPRReview.Api.Contracts;
using SmartPRReview.Api.Workers;
using SmartPRReview.Application.Reviews;
using SmartPRReview.Domain.Reviews;
using SmartPRReview.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ReviewService>();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHostedService<ReviewWorker>();
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

app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "SmartPRReview API v1");
    options.DocumentTitle = "SmartPRReview API";
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .WithName("HealthCheck")
    .WithTags("System");

var reviews = app.MapGroup("/api/reviews");

reviews.MapPost("/", async (
    CreateReviewRequest request,
    ReviewService service,
    CancellationToken cancellationToken) =>
{
    var errors = Validate(request);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var repository = new RepositoryReference(
        request.Provider,
        request.Location.Trim(),
        request.PullRequestNumber,
        request.BaseReference,
        request.HeadReference);

    var review = await service.CreateAsync(
        new CreateReviewCommand(repository),
        cancellationToken);

    var location = $"/api/reviews/{review.Id}";
    return Results.Accepted(location, review);
})
    .WithName("CreateReview")
    .WithSummary("Queue a repository for review")
    .WithDescription("Receives the repository as a request parameter and returns a cached review identifier.")
    .Produces<Review>(StatusCodes.Status202Accepted)
    .ProducesValidationProblem();

reviews.MapGet("/{id:guid}", async (
    Guid id,
    ReviewService service,
    CancellationToken cancellationToken) =>
{
    var review = await service.GetAsync(id, cancellationToken);
    return review is null ? Results.NotFound() : Results.Ok(review);
})
    .WithName("GetReview")
    .WithSummary("Get a cached review")
    .Produces<Review>()
    .Produces(StatusCodes.Status404NotFound);

app.Run();

static Dictionary<string, string[]> Validate(CreateReviewRequest request)
{
    var errors = new Dictionary<string, string[]>();

    if (string.IsNullOrWhiteSpace(request.Location))
    {
        errors[nameof(request.Location)] = ["Repository location is required."];
    }

    if (request.PullRequestNumber is <= 0)
    {
        errors[nameof(request.PullRequestNumber)] = ["Pull request number must be greater than zero."];
    }

    return errors;
}

public partial class Program;
