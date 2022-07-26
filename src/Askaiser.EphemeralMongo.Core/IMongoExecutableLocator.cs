namespace Askaiser.EphemeralMongo.Core;

internal interface IMongoExecutableLocator
{
    string? FindMongoExecutablePath();
}