using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IntegrationOps.Api.Data;

public sealed class DatabaseReadinessCheck(IntegrationOpsDbContext database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Select every required column, even from an empty table. Connectivity alone is insufficient.
        // The health-check service maps failures/timeouts to Unhealthy; the HTTP writer exposes only status.
        await database.IntegrationRuns.AsNoTracking()
            .OrderBy(run => run.ReceivedAt).ThenBy(run => run.Id)
            .Take(1).ToArrayAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
