using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using IntegrationOps.Api.Data;
using IntegrationOps.Api.IntegrationRuns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace IntegrationOps.Api.Tests;

[Collection(PostgresFixture.CollectionName)]
public sealed class SyntheticSeedTests(PostgresFixture postgres)
{
    private static readonly Guid HarborId = Guid.Parse("14ac8db6-74c5-45fb-a022-5c06b113eb03");

    [Fact]
    public async Task Seed_inserts_three_canonical_rows_and_an_exact_repeat_is_a_no_op()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        Assert.Equal(3, await SeedAsync(database));
        await using var reader = database.CreateContext();
        var first = (await reader.IntegrationRuns.AsNoTracking().OrderBy(run => run.Id).ToArrayAsync())
            .Select(IntegrationRunResponse.FromPersistence).ToArray();
        Assert.Equal(0, await SeedAsync(database));
        var second = (await reader.IntegrationRuns.AsNoTracking().OrderBy(run => run.Id).ToArrayAsync())
            .Select(IntegrationRunResponse.FromPersistence).ToArray();
        Assert.Equal(first, second);
        Assert.Equal(3, second.Length);
        Assert.All(await reader.IntegrationRuns.AsNoTracking().ToArrayAsync(), run =>
        {
            Assert.Null(run.IdempotencyKeyHash);
            Assert.Null(run.RequestFingerprint);
        });
        // Exact values for every canonical field are independently asserted by IntegrationRunsApiTests.
    }

    [Fact]
    public async Task Partial_seed_inserts_missing_rows_and_preserves_unrelated_data()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var unrelated = new IntegrationRun
        {
            Id = Guid.Parse("ec6cb282-b71c-4c7b-8ec7-9180fa65d0bb"),
            Partner = "Synthetic extra",
            Operation = "Synthetic extra operation",
            Status = IntegrationRunStatus.Pending,
            AttemptCount = 0,
            RecordCount = 0,
            ReceivedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            LastError = null,
            IdempotencyKeyHash = new byte[32],
            RequestFingerprint = Enumerable.Repeat((byte)1, 32).ToArray()
        };
        await using (var writer = database.CreateContext())
        {
            writer.AddRange(Harbor(), unrelated);
            await writer.SaveChangesAsync();
        }

        Assert.Equal(2, await SeedAsync(database));
        Assert.Equal(0, await SeedAsync(database));
        await using var reader = database.CreateContext();
        Assert.Equal(4, await reader.IntegrationRuns.CountAsync());
        var storedUnrelated = await reader.IntegrationRuns.AsNoTracking().SingleAsync(run => run.Id == unrelated.Id);
        Assert.Equal(unrelated.IdempotencyKeyHash, storedUnrelated.IdempotencyKeyHash);
        Assert.Equal(unrelated.RequestFingerprint, storedUnrelated.RequestFingerprint);
        Assert.Equal(IntegrationRunResponse.FromPersistence(unrelated),
            IntegrationRunResponse.FromPersistence(await reader.IntegrationRuns.SingleAsync(run => run.Id == unrelated.Id)));
        Assert.Equal(IntegrationRunResponse.FromPersistence(Harbor()),
            IntegrationRunResponse.FromPersistence(await reader.IntegrationRuns.SingleAsync(run => run.Id == HarborId)));
    }

    [Fact]
    public async Task Conflicting_canonical_content_leaves_the_database_unchanged()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var conflict = Harbor();
        conflict.Partner = "Conflicting synthetic value";
        await using (var writer = database.CreateContext())
        {
            writer.Add(conflict);
            await writer.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(database));
        await using var reader = database.CreateContext();
        var stored = Assert.Single(await reader.IntegrationRuns.AsNoTracking().ToArrayAsync());
        Assert.Equal(IntegrationRunResponse.FromPersistence(conflict), IntegrationRunResponse.FromPersistence(stored));
        Assert.Equal(0L, await database.ScalarAsync(
            "SELECT count(*) FROM integration_runs WHERE id <> '14ac8db6-74c5-45fb-a022-5c06b113eb03'"));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Seed_service_rejects_non_development_before_database_work(string environment)
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(database, environment));
        Assert.Equal(true, await database.ScalarAsync("SELECT to_regclass('integration_runs') IS NULL"));
    }

    [Theory]
    [InlineData("Development", 0, 3L)]
    [InlineData("Production", 1, 0L)]
    public async Task Seed_command_exits_without_starting_an_http_listener(
        string environment, int exitCode, long rowCount)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        using var occupiedPort = new TcpListener(IPAddress.Loopback, 0);
        occupiedPort.Start();
        var port = ((IPEndPoint)occupiedPort.LocalEndpoint).Port;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--seed-synthetic");
        start.ArgumentList.Add("--environment");
        start.ArgumentList.Add(environment);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add($"http://127.0.0.1:{port}");
        start.Environment["ConnectionStrings__IntegrationOps"] = database.ConnectionString;
        start.Environment["DOTNET_ENVIRONMENT"] = environment;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = environment;

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(exitCode, process.ExitCode);
            Assert.Contains(environment == "Development" ? "Synthetic seed completed." : "Synthetic seed failed.",
                await output + await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        Assert.Equal(rowCount, await database.ScalarAsync("SELECT count(*) FROM integration_runs"));
    }

    private static IntegrationRun Harbor() => new()
    {
        Id = HarborId,
        Partner = "Harbor Payments",
        Operation = "Settlement import",
        Status = IntegrationRunStatus.Pending,
        AttemptCount = 1,
        RecordCount = 0,
        ReceivedAt = new DateTime(2026, 8, 20, 8, 4, 9, DateTimeKind.Utc)
    };

    private static async Task<int> SeedAsync(TestDatabase database, string environment = "Development")
    {
        await using var context = database.CreateContext();
        return await new SyntheticSeed(context, new SeedEnvironment { EnvironmentName = environment }).RunAsync();
    }

    private sealed class SeedEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "IntegrationOps.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
