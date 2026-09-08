using IntegrationOps.Api.Data;

namespace IntegrationOps.Api.Tests;

public sealed class UtcTimestampTests
{
    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Non_utc_persistence_values_are_rejected(DateTimeKind kind)
    {
        var value = new DateTime(2026, 9, 7, 12, 0, 0, kind);
        Assert.Throws<ArgumentException>(() => UtcTimestamp.NormalizeUtc(value));
        Assert.Throws<InvalidOperationException>(() => UtcTimestamp.ToResponse(value));
        Assert.Throws<ArgumentException>(() => new IntegrationRun
        {
            Partner = "Synthetic",
            Operation = "Test",
            ReceivedAt = value
        });
        var run = new IntegrationRun
        {
            Partner = "Synthetic",
            Operation = "Test",
            ReceivedAt = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc)
        };
        Assert.Throws<ArgumentException>(() => run.CompletedAt = value);
    }

    [Theory]
    [InlineData(1L, 0L)]
    [InlineData(9L, 0L)]
    [InlineData(10L, 10L)]
    [InlineData(19L, 10L)]
    public void Normalization_truncates_without_rounding(long input, long expected)
    {
        var normalized = UtcTimestamp.NormalizeUtc(new DateTime(input, DateTimeKind.Utc));
        Assert.Equal(expected, normalized.Ticks);
        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
    }

    [Fact]
    public void Maximum_finite_timestamp_normalizes_without_overflow()
    {
        var maximum = new DateTime(DateTime.MaxValue.Ticks, DateTimeKind.Utc);
        var normalized = UtcTimestamp.NormalizeUtc(maximum);
        Assert.Equal(DateTime.MaxValue.Ticks - 9, normalized.Ticks);
        Assert.Equal(TimeSpan.Zero, UtcTimestamp.ToResponse(normalized).Offset);
    }

    [Fact]
    public void Offset_conversion_preserves_the_normalized_utc_instant_and_nulls()
    {
        var input = new DateTimeOffset(2026, 9, 7, 14, 0, 0, TimeSpan.FromHours(2)).AddTicks(1234567);
        var persisted = UtcTimestamp.ToPersistence(input);
        Assert.Equal(new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234560), persisted);
        Assert.Equal(input.UtcTicks - 7, UtcTimestamp.ToResponse(persisted).UtcTicks);
        Assert.Equal(TimeSpan.Zero, UtcTimestamp.ToResponse(persisted).Offset);
        Assert.Null(UtcTimestamp.ToPersistence((DateTimeOffset?)null));
        Assert.Null(UtcTimestamp.ToResponse((DateTime?)null));
    }
}
