namespace EphemeralMongo;

internal interface ITimeProvider
{
    DateTimeOffset UtcNow { get; }
}