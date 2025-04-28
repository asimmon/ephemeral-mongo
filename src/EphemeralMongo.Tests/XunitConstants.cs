namespace EphemeralMongo.Tests;

internal sealed class XunitConstants
{
    public const string Category = nameof(Category);

    // Tests on GitHub Actions with the Windows runner are 4x slower than on Linux
    public const string SlowOnWindows = nameof(SlowOnWindows);
}