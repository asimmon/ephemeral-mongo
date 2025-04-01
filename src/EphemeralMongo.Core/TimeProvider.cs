namespace EphemeralMongo;

internal sealed class TimeProvider : ITimeProvider
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}