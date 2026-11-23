using System.Reflection;
using Xunit;

namespace Viff.NeoRadar.Tests.Common;

public sealed class VersionTests
{
    [Fact]
    public void ManifestVersion_MatchesTheAssemblyVersion()
    {
        string? assembly = typeof(ViffCdmPlugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // The informational version may carry "+<commit>"; the release number before it must be the manifest's.
        Assert.Equal(ViffCdmPlugin.PluginVersion, assembly?.Split('+')[0]);
    }
}
