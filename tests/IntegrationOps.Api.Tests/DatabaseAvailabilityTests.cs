using IntegrationOps.Api.IntegrationRuns;

namespace IntegrationOps.Api.Tests;

public sealed class DatabaseAvailabilityTests
{
    [Fact]
    public void Application_defects_and_cancellation_are_not_database_unavailability()
    {
        Assert.False(IntegrationRunEndpoints.IsDatabaseUnavailable(
            new InvalidOperationException("Synthetic application defect.")));
        Assert.False(IntegrationRunEndpoints.IsDatabaseUnavailable(
            new InvalidOperationException("Synthetic wrapper.", new ArgumentException("Synthetic invalid argument."))));
        Assert.False(IntegrationRunEndpoints.IsDatabaseUnavailable(new OperationCanceledException()));
        Assert.False(IntegrationRunEndpoints.IsDatabaseUnavailable(
            new InvalidOperationException("Synthetic wrapper.", new OperationCanceledException())));
    }
}
