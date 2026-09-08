using IntegrationOps.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IntegrationOps.Api.Tests;

[CollectionDefinition(PostgresFixture.CollectionName, DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

public sealed class PostgresFixture : IAsyncLifetime
{
    public const string CollectionName = "PostgreSQL";
    public const string Image = "postgres:18.6-bookworm@sha256:1c59e2c3c818eaa0f0628f695b36e7c9e362d6b219b36a54a32df645cbd7e1af";
    private readonly PostgreSqlContainer _container;
    private readonly string _applicationPassword = Guid.NewGuid().ToString("N");

    public PostgresFixture()
    {
        AppContext.SetSwitch("Npgsql.DisableDateTimeInfinityConversions", true);
        _container = new PostgreSqlBuilder(Image)
            .WithDatabase("postgres")
            .WithUsername("postgres")
            .WithPassword(Guid.NewGuid().ToString("N"))
            .Build();
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
            await using var connection = new NpgsqlConnection(_container.GetConnectionString());
            await connection.OpenAsync();
            // This literal is generated internally from a GUID's fixed hexadecimal alphabet.
            await using var command = new NpgsqlCommand(
                $"CREATE ROLE integration_ops LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD '{_applicationPassword}'",
                connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "PostgreSQL tests require an accessible Linux Docker engine and the pinned image. Start Docker and rerun; database tests are never skipped.",
                exception);
        }
    }

    public async Task<TestDatabase> CreateDatabaseAsync(bool migrate = true)
    {
        var name = "integration_ops_test_" + Guid.NewGuid().ToString("N");
        await ExecuteAdminAsync($"CREATE DATABASE {name} OWNER integration_ops");
        var database = new TestDatabase(this, name, GetApplicationConnectionString(name));
        try
        {
            if (migrate)
            {
                await using var context = database.CreateContext();
                await context.Database.MigrateAsync();
            }

            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public string GetApplicationConnectionString(string databaseName) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = databaseName,
            Username = "integration_ops",
            Password = _applicationPassword,
            Pooling = false,
            Timeout = 2,
            CommandTimeout = 10,
            ApplicationName = "IntegrationOps.Tests"
        }.ConnectionString;

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        _container.PauseAsync(cancellationToken);

    public Task UnpauseAsync(CancellationToken cancellationToken = default) =>
        _container.UnpauseAsync(cancellationToken);

    public Task StopAsync() => _container.StopAsync();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _container.StartAsync(cancellationToken);
        var settings = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            // Probe a new physical connection through Docker's resolved host and mapped port.
            Pooling = false,
            Timeout = 2
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        Exception? lastConnectionException = null;
        try
        {
            while (true)
            {
                try
                {
                    await using var connection = new NpgsqlConnection(settings.ConnectionString);
                    await connection.OpenAsync(deadline.Token);
                    deadline.Token.ThrowIfCancellationRequested();
                    return;
                }
                catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
                {
                    lastConnectionException = exception;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
            }
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                "PostgreSQL did not become reachable from the test process within 30 seconds after container restart.",
                lastConnectionException ?? exception);
        }
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    internal Task DropDatabaseAsync(string name) =>
        ExecuteAdminAsync($"DROP DATABASE {name} WITH (FORCE)");

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed class TestDatabase(PostgresFixture owner, string name, string connectionString) : IAsyncDisposable
{
    public string Name { get; } = name;
    public string ConnectionString { get; } = connectionString;

    public IntegrationOpsDbContext CreateContext() => new(
        new DbContextOptionsBuilder<IntegrationOpsDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task<int> ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    public ValueTask DisposeAsync() => new(owner.DropDatabaseAsync(Name));
}
