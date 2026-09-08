using System.Text.Json.Serialization;
using IntegrationOps.Api.Data;
using IntegrationOps.Api.Processing;
using IntegrationOps.Api.IntegrationRuns;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Npgsql;

AppContext.SetSwitch("Npgsql.DisableDateTimeInfinityConversions", true);

var seedRequested = args.Contains("--seed-synthetic", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--seed-synthetic").ToArray());

builder.Services.AddDbContext<IntegrationOpsDbContext>((services, options) =>
{
    var connection = services.GetRequiredService<IConfiguration>().GetConnectionString("IntegrationOps");
    if (string.IsNullOrWhiteSpace(connection))
    {
        throw new InvalidOperationException("Configure ConnectionStrings:IntegrationOps using User Secrets or process environment.");
    }

    var settings = new NpgsqlConnectionStringBuilder(connection);
    if (settings.Username != "integration_ops")
    {
        throw new InvalidOperationException("The application requires the dedicated integration_ops login.");
    }

    options.UseNpgsql(settings.ConnectionString, postgres => postgres.CommandTimeout(10));
});
var processingEnabled = builder.Configuration.GetValue<bool>("Processing:Enabled");
if (processingEnabled && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("Synthetic processing requires Development.");
}

builder.Services.AddScoped<IntegrationRunProcessing>();
if (processingEnabled)
{
    builder.Services.AddSingleton<IIntegrationRunProcessor, SyntheticIntegrationRunProcessor>();
    builder.Services.AddHostedService<IntegrationRunWorker>();
}
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = ProcessingPolicy.ShutdownTimeout);
builder.Services.AddScoped<SyntheticSeed>();
builder.Services.AddScoped<IntegrationRunIntake>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadinessCheck>("database", tags: ["ready"], timeout: TimeSpan.FromSeconds(3));
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<IntegrationRunStatus>()));

await using var app = builder.Build();

if (seedRequested)
{
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancel;
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var count = await scope.ServiceProvider.GetRequiredService<SyntheticSeed>().RunAsync(cancellation.Token);
        Console.WriteLine($"Synthetic seed completed. Inserted {count} canonical rows.");
        return 0;
    }
    catch (Exception)
    {
        Console.Error.WriteLine("Synthetic seed failed. Use Development, apply migrations, and resolve any canonical row conflicts. Existing rows were not overwritten.");
        return 1;
    }
    finally
    {
        Console.CancelKeyPress -= cancel;
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    app.UseHsts();
}

app.UseExceptionHandler(error => error.Run(context =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
        title: "An unexpected error occurred.").ExecuteAsync(context);
}));
app.UseHttpsRedirection();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapIntegrationRuns();

await app.RunAsync();
return 0;

public partial class Program;
