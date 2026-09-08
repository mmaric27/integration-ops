using IntegrationOps.Api.IntegrationRuns;
using Microsoft.EntityFrameworkCore;

namespace IntegrationOps.Api.Data;

public sealed class SyntheticSeed(IntegrationOpsDbContext database, IHostEnvironment environment)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Synthetic seeding requires Development.");
        }

        IntegrationRun[] canonical =
        [
            new()
            {
                Id = Guid.Parse("2b31a678-fcb8-4f8b-83d0-b22eaafc9fc7"),
                Partner = "Orion Booking", Operation = "Reservation export", Status = IntegrationRunStatus.Succeeded,
                AttemptCount = 1, RecordCount = 184,
                ReceivedAt = new DateTime(2026, 8, 20, 7, 42, 18, DateTimeKind.Utc),
                CompletedAt = new DateTime(2026, 8, 20, 7, 42, 51, DateTimeKind.Utc)
            },
            new()
            {
                Id = Guid.Parse("14ac8db6-74c5-45fb-a022-5c06b113eb03"),
                Partner = "Harbor Payments", Operation = "Settlement import", Status = IntegrationRunStatus.Pending,
                AttemptCount = 1, RecordCount = 0,
                ReceivedAt = new DateTime(2026, 8, 20, 8, 4, 9, DateTimeKind.Utc)
            },
            new()
            {
                Id = Guid.Parse("7c02116a-d6b7-41f3-8528-cbfc6813eb39"),
                Partner = "Atlas Inventory", Operation = "Availability sync", Status = IntegrationRunStatus.Failed,
                AttemptCount = 3, RecordCount = 0,
                ReceivedAt = new DateTime(2026, 8, 20, 8, 11, 32, DateTimeKind.Utc),
                CompletedAt = new DateTime(2026, 8, 20, 8, 13, 5, DateTimeKind.Utc),
                LastError = "Partner endpoint returned HTTP 503 after three attempts."
            }
        ];
        var ids = canonical.Select(run => run.Id).ToArray();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var existing = await database.IntegrationRuns.AsNoTracking()
            .Where(run => ids.Contains(run.Id)).ToDictionaryAsync(run => run.Id, cancellationToken);

        foreach (var sample in canonical)
        {
            if (existing.TryGetValue(sample.Id, out var stored) &&
                IntegrationRunResponse.FromPersistence(stored) != IntegrationRunResponse.FromPersistence(sample))
            {
                throw new InvalidOperationException("A canonical synthetic row has conflicting content. No seed changes were committed.");
            }
        }

        var missing = canonical.Where(run => !existing.ContainsKey(run.Id)).ToArray();
        database.IntegrationRuns.AddRange(missing);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return missing.Length;
    }
}
