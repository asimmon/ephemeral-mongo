using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EphemeralMongo.Tests;

public class KillOnParentExitTests
{
    [Fact]
    public void EnableIfSupported_Sets_KillOnParentExit_Only_On_Linux_With_Supported_Runtime()
    {
        var processStartInfo = new ProcessStartInfo();

        KillOnParentExit.EnableIfSupported(processStartInfo);

        var property = typeof(ProcessStartInfo).GetProperty("KillOnParentExit", typeof(bool));
        var isSupported = property != null && RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        Assert.Equal(isSupported, KillOnParentExit.IsSupported);

        if (property != null)
        {
            Assert.Equal(isSupported, (bool)property.GetValue(processStartInfo)!);
        }
    }
}
