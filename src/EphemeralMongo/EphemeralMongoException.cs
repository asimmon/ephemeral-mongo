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
}