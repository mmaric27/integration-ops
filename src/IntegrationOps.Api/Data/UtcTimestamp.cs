namespace IntegrationOps.Api.Data;

public static class UtcTimestamp
{
    public static DateTime NormalizeUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("A UTC DateTime is required.", nameof(value));
        }

        const long ticksPerMicrosecond = 10;
        return new DateTime(value.Ticks - value.Ticks % ticksPerMicrosecond, DateTimeKind.Utc);
    }

    public static DateTime ToPersistence(DateTimeOffset value) => NormalizeUtc(value.UtcDateTime);

    public static DateTime? ToPersistence(DateTimeOffset? value) =>
        value.HasValue ? ToPersistence(value.Value) : null;

    public static DateTimeOffset ToResponse(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new InvalidOperationException("A persisted timestamp must be UTC.");
        }

        return new DateTimeOffset(value, TimeSpan.Zero);
    }

    public static DateTimeOffset? ToResponse(DateTime? value) =>
        value.HasValue ? ToResponse(value.Value) : null;
}
