using System.Diagnostics.CodeAnalysis;

namespace EphemeralMongo;

[SuppressMessage("Design", "CA1008:Enums should have zero value", Justification = "Doesn't make sense in this context")]
public enum MongoVersion
{
    V6 = 6,
    V7 = 7,
    V8 = 8,
}