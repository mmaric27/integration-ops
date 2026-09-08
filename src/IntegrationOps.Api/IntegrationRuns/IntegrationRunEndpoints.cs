using System.Data.Common;
using IntegrationOps.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("IntegrationOps.Api.Tests")]

namespace IntegrationOps.Api.IntegrationRuns;

public static class IntegrationRunEndpoints
{
    public static void MapIntegrationRuns(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/integration-runs", GetAsync)
            .WithName("GetIntegrationRuns")
            .Produces<IntegrationRunResponse[]>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet("/api/integration-runs/{id}", GetByIdAsync)
            .WithName("GetIntegrationRunById")
            .Produces<IntegrationRunResponse>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(503).ProducesProblem(500)
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                foreach (var parameter in operation.Parameters ?? [])
                {
                    if (parameter.Name == "id" && parameter is OpenApiParameter identifier)
                    {
                        identifier.Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" };
                    }
                }
                return Task.CompletedTask;
            });

        endpoints.MapPost("/api/integration-runs", PostAsync)
            .WithName("CreateIntegrationRun")
            .Produces<IntegrationRunResponse>(201).Produces<IntegrationRunResponse>(200)
            .ProducesProblem(400).ProducesProblem(409).ProducesProblem(413)
            .ProducesProblem(415).ProducesProblem(503).ProducesProblem(500)
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Description = "Durable synthetic intake; no inline processing. " +
                    "An opt-in Development-only background worker can process committed runs. New run: 201 with Location. " +
                    "Same key and normalized request: 200 with CURRENT run state; different request: 409. " +
                    "All application responses use Cache-Control: no-store. Transport body limit: 8192 bytes. " +
                    "Only application/json with absent or UTF-8 charset; absent or identity Content-Encoding. " +
                    "Reject duplicate/unknown properties, malformed Unicode, comments and trailing commas. " +
                    "Trim Unicode whitespace and normalize labels to NFC before the 1-160 scalar limit; " +
                    "reject Cc, U+2028 and U+2029 before trimming. Preserve case and internal permitted whitespace.";
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "Idempotency-Key",
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "One case-sensitive value, globally scoped for this local application. No whitespace or normalization. " +
                        "Retained for the run lifetime. Random UUIDs recommended. Never use secrets.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 128, Pattern = "^[A-Za-z0-9._-]+$" }
                });
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new()
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = JsonSchemaType.Object,
                                AdditionalPropertiesAllowed = false,
                                Required = new HashSet<string> { "partner", "operation" },
                                Properties = new Dictionary<string, IOpenApiSchema>
                                {
                                    ["partner"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Required label; 1-160 Unicode scalars AFTER trimming and NFC normalization." },
                                    ["operation"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Required label; 1-160 Unicode scalars AFTER trimming and NFC normalization." }
                                }
                            }
                        }
                    }
                };
                if (operation.Responses?["201"] is OpenApiResponse created)
                {
                    created.Headers ??= new Dictionary<string, IOpenApiHeader>();
                    created.Headers["Location"] = new OpenApiHeader
                    {
                        Description = "URI of GetIntegrationRunById for the created run.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri-reference" }
                    };
                }
                return Task.CompletedTask;
            });
    }

    private static async Task<Results<Ok<IntegrationRunResponse[]>, ProblemHttpResult>> GetAsync(
        HttpContext context, IntegrationOpsDbContext database, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            // Order in SQL; explicitly map the public response, excluding intake metadata.
            var runs = await database.IntegrationRuns.AsNoTracking()
                .OrderBy(run => run.ReceivedAt).ThenBy(run => run.Id)
                .ToArrayAsync(cancellationToken);
            return TypedResults.Ok(runs.Select(IntegrationRunResponse.FromPersistence).ToArray());
        }
        catch (Exception exception) when (IsDatabaseUnavailable(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Integration data is unavailable.");
        }
    }

    private static async Task<IResult> GetByIdAsync(
        string id, HttpContext context, IntegrationOpsDbContext database, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(id, out var identifier))
        {
            return TypedResults.Problem(statusCode: 400, title: "Invalid integration-run identifier.");
        }

        try
        {
            var run = await database.IntegrationRuns.AsNoTracking()
                .SingleOrDefaultAsync(value => value.Id == identifier, cancellationToken);
            return run is null
                ? TypedResults.Problem(statusCode: 404, title: "Integration run was not found.")
                : TypedResults.Ok(IntegrationRunResponse.FromPersistence(run));
        }
        catch (Exception exception) when (IsDatabaseUnavailable(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return IntakeProblem(503);
        }
    }

    private static async Task<IResult> PostAsync(
        HttpContext context, IntegrationRunIntake intake, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var (input, error) = await IntegrationRunIntakeInput.ReadAsync(context.Request, cancellationToken);
            if (input is null)
            {
                return IntakeProblem(error);
            }

            var (run, status) = await intake.AcceptAsync(input, cancellationToken);
            return status switch
            {
                201 => TypedResults.CreatedAtRoute(run!, "GetIntegrationRunById", new { id = run!.Id }),
                200 => TypedResults.Ok(run!),
                _ => IntakeProblem(status)
            };
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode is 400 or 413)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return IntakeProblem(exception.StatusCode);
        }
        catch (Exception exception) when (IntegrationRunIntake.IsUnavailable(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return IntakeProblem(503);
        }
    }

    private static ProblemHttpResult IntakeProblem(int status) => TypedResults.Problem(
        statusCode: status, title: status switch
        {
            400 => "Invalid integration-run request.",
            409 => "Idempotency key conflicts with an existing request.",
            413 => "Request body is too large.",
            415 => "Unsupported request content type or encoding.",
            503 => "Integration data is unavailable.",
            _ => "An unexpected error occurred."
        });

    // EF can wrap provider failures; unrelated application exceptions must still reach the 500 handler.
    internal static bool IsDatabaseUnavailable(Exception exception) => exception switch
    {
        DbException or TimeoutException => true,
        InvalidOperationException { InnerException: { } inner } => IsDatabaseUnavailable(inner),
        _ => false
    };
}
