using System.Runtime.InteropServices;

namespace EphemeralMongo.Download;

internal static class DownloadEditionHelper
{
    public static string GetEdition(MongoEdition edition)
    {
        return GetEdition(edition, RuntimeInformationHelper.OSPlatform);
    }

    public static string GetEdition(MongoEdition edition, OSPlatform platform)
    {
        if (edition == MongoEdition.Enterprise)
        {
            return "enterprise";
        }

        return platform == OSPlatform.Linux ? "targeted" : "base";
    }
}