namespace EphemeralMongo;

public sealed class EphemeralMongoException : Exception
{
    public EphemeralMongoException(string message)
        : base(message)
    {
    }

    public EphemeralMongoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    // Set when a MongoDB process exited unexpectedly, so callers can react to specific failures
    internal uint? ProcessExitCode { get; private set; }

    internal static EphemeralMongoException FromProcessExit(string message, uint exitCode)
    {
        return new EphemeralMongoException(message) { ProcessExitCode = exitCode };
    }
}