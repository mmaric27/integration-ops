using System.Net;
using System.Text;
using System.Text.Json;
using IntegrationOps.Api.IntegrationRuns;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationOps.Api.Tests;

// No database is needed to prove rejection before I/O; database behavior lives in the PostgreSQL collection.
public sealed class IntegrationRunIntakeInputTests
{
    internal const string ValidBody = """{"partner":"Synthetic Partner","operation":"Import"}""";

    public static IEnumerable<object[]> InvalidBodies()
    {
        foreach (var body in new[]
        {
            "", "null", "[]", "1", "true", "\"text\"", "{", "{}",
            """{"partner":"x"}""", """{"operation":"x"}""",
            """{"partner":null,"operation":"x"}""", """{"partner":1,"operation":"x"}""",
            """{"partner":"x","operation":false}""", """{"partner":"x","operation":{}}""",
            """{"partner":"x","operation":null}""", """{"partner":"x","operation":[]}""",
            """{"Partner":"x","operation":"x"}""",
            """{"partner":"x","partner":"x","operation":"x"}""",
            """{"partner":"x","operation":"x","oper\u0061tion":"x"}""",
            """{"partner":"x","operation":"x",}""",
            """{/*comment*/"partner":"x","operation":"x"}""",
            """{"partner":"x","operation":"x"}{}""",
            """{"partner":"\uD800","operation":"x"}""",
            """{"partner":"\uDC00","operation":"x"}""",
            """{"partner":"\uD800x","operation":"x"}""",
            """{"partner":"\uD800\uD800","operation":"x"}""",
            """{"partner":"x","operation":"\uDC00\uD800"}""",
            """{"partner":"x","operation":"x","\uD800":"x"}"""
        })
        {
            yield return [body];
        }

        foreach (var property in new[] { "id", "status", "attemptCount", "recordCount", "receivedAt", "completedAt", "lastError", "idempotencyKeyHash", "requestFingerprint", "unknown" })
        {
            yield return [ValidBody[..^1] + ",\"" + property + "\":null}"];
        }

        foreach (var label in new[] { "", " \u2003 ", "\tx", "x\n", "x\0y", "x\u007Fy", "x\u0085y", "x\u2028y", "x\u2029y", new string('x', 161), string.Concat(Enumerable.Repeat("\U0001F680", 161)) })
        {
            yield return [JsonSerializer.Serialize(new { partner = label, operation = "Import" })];
            yield return [JsonSerializer.Serialize(new { partner = "Synthetic", operation = label })];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Invalid_envelopes_and_labels_return_safe_400(string body)
    {
        Assert.Null(IntegrationRunIntakeInput.ParseBody(Encoding.UTF8.GetBytes(body)));
        using var response = await SendAsync(body);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData(" padded ")]
    [InlineData("a,b")]
    [InlineData("a/b")]
    [InlineData("café")]
    public async Task Invalid_keys_return_safe_400(string? key)
    {
        using var response = await SendAsync(ValidBody, key);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Oversized_and_multiple_keys_are_rejected()
    {
        using var oversized = await SendAsync(ValidBody, new string('a', 129));
        await AssertProblemAsync(oversized, HttpStatusCode.BadRequest);
        var context = Request(ValidBody);
        context.Request.Headers["Idempotency-Key"] = new[] { "first", "second" };
        Assert.Equal(400, (await IntegrationRunIntakeInput.ReadAsync(context.Request, default)).Error);
        await using var factory = Factory();
        using var client = factory.CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/integration-runs") { Content = new StringContent(ValidBody, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", new[] { "first", "second" });
        using var response = await client.SendAsync(request);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("text/plain", null)]
    [InlineData("application/problem+json", null)]
    [InlineData("application/json; charset=utf-16", null)]
    [InlineData("application/json; charset=iso-8859-1", null)]
    [InlineData("application/json", "gzip")]
    [InlineData("application/json", "br")]
    [InlineData(null, null)]
    public async Task Unsupported_transport_returns_415(string? contentType, string? encoding)
    {
        using var response = await SendAsync(ValidBody, contentType: contentType, encoding: encoding);
        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Body_limit_counts_actual_bytes_including_unknown_length(bool unknownLength)
    {
        await using var factory = Factory();
        using var client = factory.CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/integration-runs");
        request.Headers.Add("Idempotency-Key", "oversized");
        var bytes = Encoding.UTF8.GetBytes(ValidBody + new string(' ', 8193));
        request.Content = unknownLength ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        using var response = await client.SendAsync(request);
        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Exactly_8192_bytes_is_a_transport_valid_body_and_raw_invalid_utf8_is_rejected()
    {
        var context = Request(ValidBody + new string(' ', 8192 - Encoding.UTF8.GetByteCount(ValidBody)));
        Assert.NotNull((await IntegrationRunIntakeInput.ReadAsync(context.Request, default)).Input);
        byte[] invalid = [.. Encoding.UTF8.GetBytes("{\"partner\":\""), 0xC0, 0xAF, .. Encoding.UTF8.GetBytes("\",\"operation\":\"x\"}")];
        Assert.Null(IntegrationRunIntakeInput.ParseBody(invalid));
        Assert.Null(IntegrationRunIntakeInput.NormalizeLabel("\uD800"));
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=UTF-8")]
    [InlineData("application/json; charset=\"utf-8\"")]
    public async Task Valid_labels_normalize_and_accept_Cf_and_supplementary_scalars(string contentType)
    {
        foreach (var label in new[] { "\u2003Cafe\u0301\u2003", new string('x', 160), string.Concat(Enumerable.Repeat("\U0001F680", 160)), "x\u200Dy", "x\u202Ey", "a  b" })
        {
            var context = Request(JsonSerializer.Serialize(new { partner = label, operation = "Import" }));
            context.Request.ContentType = contentType;
            var result = await IntegrationRunIntakeInput.ReadAsync(context.Request, default);
            Assert.Equal(0, result.Error);
            Assert.Equal(label.Trim().Normalize(), result.Input!.Request.Partner);
        }
    }

    [Fact]
    public async Task Key_digest_uses_exact_case_sensitive_bytes_and_permits_128_characters()
    {
        var lower = Request(ValidBody); lower.Request.Headers["Idempotency-Key"] = "abc";
        var upper = Request(ValidBody); upper.Request.Headers["Idempotency-Key"] = "ABC";
        var a = (await IntegrationRunIntakeInput.ReadAsync(lower.Request, default)).Input!;
        var b = (await IntegrationRunIntakeInput.ReadAsync(upper.Request, default)).Input!;
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", Convert.ToHexString(a.KeyHash));
        Assert.False(a.KeyHash.SequenceEqual(b.KeyHash));
        lower.Request.Body.Position = 0;
        lower.Request.Headers["Idempotency-Key"] = new string('a', 125) + "._-";
        Assert.NotNull((await IntegrationRunIntakeInput.ReadAsync(lower.Request, default)).Input);
    }

    [Theory]
    [InlineData("Synthetic Partner", "Import", "D5C6EEB3AAC238773427E8E779C6E1F6C45569C068F3278A7D5FD3B883A0C12B")]
    [InlineData("Café", "同步", "7BEF6B29225747834E2F3FABA6CEBBBFC4A5207A84A1B867FAA0A16E207ABB4D")]
    public void Fingerprint_has_stable_versioned_length_framed_vectors(string partner, string operation, string expected)
    {
        Assert.Equal(expected, Convert.ToHexString(IntegrationRunIntakeInput.FingerprintFor(new(partner, operation))));
        Assert.False(IntegrationRunIntakeInput.FingerprintFor(new("ab", "c"))
            .SequenceEqual(IntegrationRunIntakeInput.FingerprintFor(new("a", "bc"))));
    }

    [Fact]
    public void Only_the_named_key_violation_enters_replay_and_integrity_errors_are_not_unavailability()
    {
        foreach (var (state, constraint, expected) in new[]
        {
            ("23505", IntegrationRunIntake.KeyIndex, true), ("23505", "pk_integration_runs", false),
            ("23514", IntegrationRunIntake.KeyIndex, false), ("23505", "other_unique_index", false)
        })
        {
            var postgres = new PostgresException("restricted diagnostic", "ERROR", "ERROR", state, constraintName: constraint);
            var error = new DbUpdateException("restricted diagnostic", new InvalidOperationException("wrapper", postgres));
            Assert.Equal(expected, IntegrationRunIntake.IsKeyViolation(error));
            Assert.False(IntegrationRunIntake.IsUnavailable(error));
        }
        Assert.False(IntegrationRunIntake.IsUnavailable(new InvalidOperationException("application defect")));
        Assert.False(IntegrationRunIntake.IsUnavailable(new OperationCanceledException()));
        Assert.True(IntegrationRunIntake.IsUnavailable(new InvalidOperationException("wrapper", new NpgsqlException("transport"))));
    }

    [Fact]
    public async Task Body_read_honors_cancellation()
    {
        var request = Request(ValidBody).Request;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IntegrationRunIntakeInput.ReadAsync(request, cancellation.Token));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-00000000000z")]
    public async Task Malformed_id_returns_controlled_400_before_database_io(string id)
    {
        await using var factory = Factory();
        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync("/api/integration-runs/" + id);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Invalid integration-run identifier.");
    }

    [Fact]
    public void Pinned_provider_translates_byte_array_sequence_equal_to_binary_sql_equality()
    {
        using var context = new IntegrationOps.Api.Data.IntegrationOpsDbContext(
            new DbContextOptionsBuilder<IntegrationOps.Api.Data.IntegrationOpsDbContext>()
                .UseNpgsql("Host=127.0.0.1;Database=not_used;Username=integration_ops").Options);
        var keyHash = new byte[32];
        var sql = context.IntegrationRuns.AsNoTracking().Where(run =>
            run.IdempotencyKeyHash != null && run.IdempotencyKeyHash.SequenceEqual(keyHash)).ToQueryString();
        Assert.Matches(@"idempotency_key_hash\s*=\s*@", sql);
    }

    [Fact]
    public async Task Intake_openapi_is_generated_without_database_io()
    {
        await using var factory = Factory();
        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        IntegrationRunsApiTests.AssertIntakeOpenApi(document.RootElement);
    }

    internal static DefaultHttpContext Request(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Headers["Idempotency-Key"] = "synthetic-key";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context;
    }

    private static DatabaseApiFactory Factory() => new("Host=127.0.0.1;Port=1;Database=not_used;Username=integration_ops;Pooling=false;Timeout=1");

    private static async Task<HttpResponseMessage> SendAsync(string body, string? key = "synthetic-key", string? contentType = "application/json", string? encoding = null)
    {
        await using var factory = Factory();
        using var client = factory.CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/integration-runs") { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        if (contentType is not null) request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (encoding is not null) request.Content.Headers.TryAddWithoutValidation("Content-Encoding", encoding);
        return await client.SendAsync(request);
    }

    internal static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expected, string? title = null)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var problem = document.RootElement;
        Assert.Equal((int)expected, problem.GetProperty("status").GetInt32());
        if (title is not null) Assert.Equal(title, problem.GetProperty("title").GetString());
        Assert.All(problem.EnumerateObject(), property => Assert.Contains(property.Name, new[] { "type", "title", "status", "traceId" }));
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
}
