using System.Diagnostics.CodeAnalysis;

namespace EphemeralMongo.Tests;

internal sealed record Person(string Id, string Name)
{
    [SuppressMessage("ReSharper", "UnusedMember.Local", Justification = "Used by MongoDB deserialization")]
    public Person()
        : this(string.Empty, string.Empty)
    {
    }
}