namespace EphemeralMongo.Core;

internal interface IPortFactory
{
    int GetRandomAvailablePort();
}