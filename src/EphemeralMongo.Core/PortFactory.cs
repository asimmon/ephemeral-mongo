using System.Net;
using System.Net.Sockets;

namespace EphemeralMongo.Core;

internal sealed class PortFactory : IPortFactory
{
    public int GetRandomAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Any, port: 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}