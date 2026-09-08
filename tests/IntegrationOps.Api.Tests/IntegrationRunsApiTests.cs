using System.Net;
using System.Text.Json;
using IntegrationOps.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationOps.Api.Tests;

[Collection(PostgresFixture.CollectionName)]
public sealed class IntegrationRunsApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private DatabaseApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _database = await postgres.CreateDatabaseAsync();
        _factory = new DatabaseApiFactory(_database.ConnectionString);
        _client = _factory.CreateHttpsClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SyntheticSeed>().RunAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Fact]
    public async Task Health_returns_healthy_status()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Integration_runs_return_expected_operational_states()
    {
        var response = await _client.GetAsync("/api/integration-runs");
        var json = await response.Content.ReadAsStringAsync();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false
        };
        var runs = JsonSerializer.Deserialize<IntegrationRunContract[]>(json, options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.NotNull(runs);
        using (var document = JsonDocument.Parse(json))
        {
            Assert.Collection(
                document.RootElement.EnumerateArray(),
                run => AssertWireContract(run, JsonValueKind.String, JsonValueKind.Null),
                run => AssertWireContract(run, JsonValueKind.Null, JsonValueKind.Null),
                run => AssertWireContract(run, JsonValueKind.String, JsonValueKind.String));
        }
        Assert.Collection(
            runs,
            run => Assert.Equal(
                new IntegrationRunContract(
                    Guid.Parse("2b31a678-fcb8-4f8b-83d0-b22eaafc9fc7"),
                    "Orion Booking",
                    "Reservation export",
                    "Succeeded",
                    1,
                    184,
                    DateTimeOffset.Parse("2026-08-20T07:42:18Z"),
                    DateTimeOffset.Parse("2026-08-20T07:42:51Z"),
                    null),
                run),
            run => Assert.Equal(
                new IntegrationRunContract(
                    Guid.Parse("14ac8db6-74c5-45fb-a022-5c06b113eb03"),
                    "Harbor Payments",
                    "Settlement import",
                    "Pending",
                    1,
                    0,
                    DateTimeOffset.Parse("2026-08-20T08:04:09Z"),
                    null,
                    null),
                run),
            run => Assert.Equal(
                new IntegrationRunContract(
                    Guid.Parse("7c02116a-d6b7-41f3-8528-cbfc6813eb39"),
                    "Atlas Inventory",
                    "Availability sync",
                    "Failed",
                    3,
                    0,
                    DateTimeOffset.Parse("2026-08-20T08:11:32Z"),
                    DateTimeOffset.Parse("2026-08-20T08:13:05Z"),
                    "Partner endpoint returned HTTP 503 after three attempts."),
                run));
    }

    [Fact]
    public async Task OpenApi_in_development_describes_the_run_response()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var operation = root.GetProperty("paths").GetProperty("/api/integration-runs").GetProperty("get");
        Assert.Equal("GetIntegrationRuns", operation.GetProperty("operationId").GetString());

        var schema = operation.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Equal("array", schema.GetProperty("type").GetString());
        var item = ResolveSchema(root, schema.GetProperty("items"));
        var properties = item.GetProperty("properties");
        Assert.Equal(ExpectedPropertyNames, GetPropertyNames(properties));

        Assert.Equal("uuid", properties.GetProperty("id").GetProperty("format").GetString());
        Assert.Equal("string", properties.GetProperty("partner").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("operation").GetProperty("type").GetString());
        Assert.Equal("date-time", properties.GetProperty("receivedAt").GetProperty("format").GetString());
        Assert.Equal("date-time", properties.GetProperty("completedAt").GetProperty("format").GetString());

        var status = ResolveSchema(root, properties.GetProperty("status"));
        Assert.Equal(
            new[] { "Failed", "Pending", "Succeeded" },
            status.GetProperty("enum").EnumerateArray().Select(value => value.GetString()).Order().ToArray());
        Assert.True(AllowsNull(properties.GetProperty("completedAt")));
        Assert.True(AllowsNull(properties.GetProperty("lastError")));
    }

    [Fact]
    public async Task OpenApi_describes_strict_intake_idempotency_responses_and_resource_address()
    {
        using var response = await _client.GetAsync("/openapi/v1.json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertIntakeOpenApi(document.RootElement);
    }

    internal static void AssertIntakeOpenApi(JsonElement root)
    {
        var paths = root.GetProperty("paths");
        var post = paths.GetProperty("/api/integration-runs").GetProperty("post");
        Assert.Equal("CreateIntegrationRun", post.GetProperty("operationId").GetString());
        Assert.Contains("no inline processing", post.GetProperty("description").GetString());
        Assert.Contains("opt-in Development-only background worker", post.GetProperty("description").GetString());
        var key = Assert.Single(post.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.Equal("header", key.GetProperty("in").GetString());
        Assert.True(key.GetProperty("required").GetBoolean());
        var keySchema = key.GetProperty("schema");
        Assert.Equal(1, keySchema.GetProperty("minLength").GetInt32());
        Assert.Equal(128, keySchema.GetProperty("maxLength").GetInt32());
        Assert.Equal("^[A-Za-z0-9._-]+$", keySchema.GetProperty("pattern").GetString());
        var body = post.GetProperty("requestBody");
        Assert.True(body.GetProperty("required").GetBoolean());
        Assert.Equal(new[] { "application/json" }, GetPropertyNames(body.GetProperty("content")));
        var schema = ResolveSchema(root, body.GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "operation", "partner" }, GetPropertyNames(schema.GetProperty("properties")));
        Assert.Equal(new[] { "operation", "partner" }, schema.GetProperty("required").EnumerateArray().Select(v => v.GetString()).Order().ToArray());
        foreach (var property in schema.GetProperty("properties").EnumerateObject()) Assert.Equal("string", property.Value.GetProperty("type").GetString());
        var responses = post.GetProperty("responses");
        foreach (var status in new[] { "200", "201" })
        {
            var run = ResolveSchema(root, responses.GetProperty(status).GetProperty("content").GetProperty("application/json").GetProperty("schema"));
            Assert.Equal(ExpectedPropertyNames, GetPropertyNames(run.GetProperty("properties")));
        }
        Assert.True(responses.GetProperty("201").GetProperty("headers").TryGetProperty("Location", out _));
        foreach (var status in new[] { "400", "409", "413", "415", "500", "503" })
            Assert.True(responses.GetProperty(status).GetProperty("content").TryGetProperty("application/problem+json", out _));
        var get = paths.GetProperty("/api/integration-runs/{id}").GetProperty("get");
        Assert.Equal("GetIntegrationRunById", get.GetProperty("operationId").GetString());
        var id = Assert.Single(get.GetProperty("parameters").EnumerateArray());
        Assert.Equal("uuid", id.GetProperty("schema").GetProperty("format").GetString());
        foreach (var status in new[] { "200", "400", "404", "500", "503" }) Assert.True(get.GetProperty("responses").TryGetProperty(status, out _));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task OpenApi_is_not_exposed_outside_development(string environment)
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Requests_with_an_unapproved_host_are_rejected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/integration-runs");
        request.Headers.Host = "untrusted.example";

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static readonly string[] ExpectedPropertyNames =
    [
        "attemptCount",
        "completedAt",
        "id",
        "lastError",
        "operation",
        "partner",
        "receivedAt",
        "recordCount",
        "status"
    ];

    private static void AssertWireContract(
        JsonElement run,
        JsonValueKind completedAtKind,
        JsonValueKind lastErrorKind)
    {
        Assert.Equal(ExpectedPropertyNames, GetPropertyNames(run));
        Assert.Equal(JsonValueKind.String, run.GetProperty("id").ValueKind);
        Assert.Equal(JsonValueKind.String, run.GetProperty("partner").ValueKind);
        Assert.Equal(JsonValueKind.String, run.GetProperty("operation").ValueKind);
        Assert.Equal(JsonValueKind.String, run.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.String, run.GetProperty("receivedAt").ValueKind);
        Assert.Equal(JsonValueKind.Number, run.GetProperty("attemptCount").ValueKind);
        Assert.Equal(JsonValueKind.Number, run.GetProperty("recordCount").ValueKind);
        Assert.Equal(completedAtKind, run.GetProperty("completedAt").ValueKind);
        Assert.Equal(lastErrorKind, run.GetProperty("lastError").ValueKind);
    }

    private static string[] GetPropertyNames(JsonElement value) =>
        value.EnumerateObject().Select(property => property.Name).Order().ToArray();

    private static JsonElement ResolveSchema(JsonElement document, JsonElement schema)
    {
        if (!schema.TryGetProperty("$ref", out var reference))
        {
            return schema;
        }

        const string prefix = "#/components/schemas/";
        var path = reference.GetString();
        Assert.NotNull(path);
        Assert.StartsWith(prefix, path);
        return document.GetProperty("components").GetProperty("schemas").GetProperty(path[prefix.Length..]);
    }

    private static bool AllowsNull(JsonElement schema) =>
        (schema.TryGetProperty("nullable", out var nullable) && nullable.ValueKind == JsonValueKind.True) ||
        (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Array &&
         type.EnumerateArray().Any(value => value.GetString() == "null"));

    private sealed record IntegrationRunContract(
        Guid Id,
        string Partner,
        string Operation,
        string Status,
        int AttemptCount,
        int RecordCount,
        DateTimeOffset ReceivedAt,
        DateTimeOffset? CompletedAt,
        string? LastError);
}
