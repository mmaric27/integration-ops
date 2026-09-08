using IntegrationOps.Api.Data;
using IntegrationOps.Api.Processing;
using Npgsql;

namespace IntegrationOps.Api.Tests;

public sealed class ProcessingPolicyTests
{
    [Fact]
    public void Policy_and_vocabulary_are_closed()
    {
        Assert.Equal(3, ProcessingPolicy.MaximumAttempts);
        Assert.Equal(TimeSpan.FromSeconds(60), ProcessingPolicy.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(10), ProcessingPolicy.ExecutionDeadline);
        Assert.Equal(TimeSpan.FromMilliseconds(500), ProcessingPolicy.IdleDelay);
        Assert.Equal(TimeSpan.FromSeconds(2), ProcessingPolicy.DatabaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), ProcessingPolicy.ShutdownTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), ProcessingPolicy.RetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(10), ProcessingPolicy.RetryDelay(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProcessingPolicy.RetryDelay(3));
        Assert.Equal(new[] { "synthetic_retryable_failure", "synthetic_permanent_failure", "processing_deadline_exceeded",
            "attempt_abandoned", "attempt_exhausted", "internal_processing_failure" },
            Enum.GetValues<ProcessingError>().Select(ProcessingResult.ErrorCode));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProcessingResult.ErrorCode((ProcessingError)999));
    }

    [Theory]
    [InlineData("Ordinary", 1, ProcessingOutcome.Success)]
    [InlineData("Synthetic:RetryOnce", 1, ProcessingOutcome.Retryable)]
    [InlineData("Synthetic:RetryOnce", 2, ProcessingOutcome.Success)]
    [InlineData("Synthetic:AlwaysRetry", 3, ProcessingOutcome.Retryable)]
    [InlineData("Synthetic:Fail", 1, ProcessingOutcome.Permanent)]
    [InlineData("synthetic:fail", 1, ProcessingOutcome.Success)]
    public async Task Synthetic_execution_is_deterministic_and_ordinal(string operation, int number, ProcessingOutcome expected)
    {
        var result = await new SyntheticIntegrationRunProcessor().ExecuteAsync(
            new ProcessingAttempt(Guid.NewGuid(), "Synthetic", operation, number, Guid.NewGuid()), CancellationToken.None);
        Assert.Equal(expected, result.Outcome);
        Assert.True(result.IsValidProcessorResult());
        Assert.Equal(expected == ProcessingOutcome.Success ? 1 : 0, result.RecordCount);
    }

    [Theory]
    [InlineData(ProcessingOutcome.Success, -1, null)]
    [InlineData(ProcessingOutcome.Success, 1, ProcessingError.InternalProcessingFailure)]
    [InlineData(ProcessingOutcome.Permanent, 1, ProcessingError.SyntheticPermanentFailure)]
    [InlineData(ProcessingOutcome.Retryable, 0, ProcessingError.AttemptAbandoned)]
    [InlineData(ProcessingOutcome.Permanent, 0, ProcessingError.AttemptExhausted)]
    [InlineData((ProcessingOutcome)99, 0, null)]
    public void Invalid_processor_results_are_rejected(ProcessingOutcome outcome, int records, ProcessingError? error) =>
        Assert.False(new ProcessingResult(outcome, records, error).IsValidProcessorResult());

    [Theory]
    [InlineData("08006", true)]
    [InlineData("57P01", true)]
    [InlineData("53300", true)]
    [InlineData("42P01", false)]
    [InlineData("42703", false)]
    [InlineData("23514", false)]
    [InlineData("23505", false)]
    [InlineData("57014", false)]
    public void Server_failures_are_classified_before_the_provider_base_type(string code, bool expected) =>
        Assert.Equal(expected, IntegrationRunProcessing.IsDatabaseUnavailable(new PostgresException("private", "ERROR", "ERROR", code)));

    [Fact]
    public void Cancellation_and_programming_failures_are_not_outages()
    {
        Assert.False(IntegrationRunProcessing.IsDatabaseUnavailable(new OperationCanceledException()));
        Assert.False(IntegrationRunProcessing.IsDatabaseUnavailable(new InvalidOperationException()));
        Assert.False(IntegrationRunProcessing.IsDatabaseUnavailable(new NpgsqlException("Unknown provider failure")));
        Assert.True(IntegrationRunProcessing.IsDatabaseUnavailable(new InvalidOperationException("wrapper", new NpgsqlException("transport", new TimeoutException()))));
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Scheduling_properties_reject_non_utc(DateTimeKind kind)
    {
        var row = new IntegrationRun { Partner = "Synthetic", Operation = "Test", ReceivedAt = DateTime.UtcNow };
        Assert.Throws<ArgumentException>(() => row.LeaseExpiresAt = new DateTime(2026, 1, 1, 0, 0, 0, kind));
        Assert.Throws<ArgumentException>(() => row.NextAttemptAt = new DateTime(2026, 1, 1, 0, 0, 0, kind));
    }
}

public sealed class ProcessingActivationTests
{
    private const string ModelConnection = "Host=127.0.0.1;Port=1;Database=synthetic_activation;Username=integration_ops;Timeout=1;Pooling=false";

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Worker_is_absent_when_configuration_is_missing_or_disabled(bool? enabled)
    {
        await using var factory = new DatabaseApiFactory(ModelConnection, enabled);
        using var client = factory.CreateHttpsClient();
        var configuration = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(factory.Services);
        Assert.Equal(enabled?.ToString(), configuration["Processing:Enabled"]);
        using var response = await client.GetAsync("/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetServices<Microsoft.Extensions.Hosting.IHostedService>(factory.Services), service => service is IntegrationRunWorker);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Enabling_synthetic_processing_outside_development_is_rejected(string environment)
    {
        await using var factory = new DatabaseApiFactory(ModelConnection, true, environment: environment);
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateHttpsClient());
        Assert.Contains("Synthetic processing requires Development.", error.ToString());
    }

    [Fact]
    public async Task Development_opt_in_registers_one_worker_and_keeps_stop_host_behavior()
    {
        await using var factory = new DatabaseApiFactory(ModelConnection, true);
        using var client = factory.CreateHttpsClient();
        var hosted = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetServices<Microsoft.Extensions.Hosting.IHostedService>(factory.Services);
        Assert.Single(hosted.OfType<IntegrationRunWorker>());
        var options = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Hosting.HostOptions>>(factory.Services).Value;
        Assert.Equal(Microsoft.Extensions.Hosting.BackgroundServiceExceptionBehavior.StopHost, options.BackgroundServiceExceptionBehavior);
        Assert.Equal(ProcessingPolicy.ShutdownTimeout, options.ShutdownTimeout);
    }
}
