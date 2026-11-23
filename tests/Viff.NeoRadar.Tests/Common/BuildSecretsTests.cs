using System;
using System.IO;
using System.Linq;
using Viff.NeoRadar.Common;
using Xunit;

namespace Viff.NeoRadar.Tests.Common;

/// <summary>
/// The built-in key is stored masked; these check it decodes to the key the build read. No message names the key.
/// </summary>
public sealed class BuildSecretsTests
{
    [Fact]
    public void ApiKey_DecodesToTheKeyTheBuildRead()
    {
        string expected = ExpectedKey();

        Assert.True(string.Equals(expected, BuildSecrets.ApiKey, StringComparison.Ordinal),
            $"The built-in key ({BuildSecrets.ApiKey.Length} characters) does not decode to the build's key ({expected.Length} characters)");
    }

    // As the build reads it: VIFF_API_KEY, else .env at the repository root, else none.
    private static string ExpectedKey()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("VIFF_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Clean(fromEnvironment);
        }

        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (Directory.Exists(Path.Combine(folder.FullName, ".git")) || File.Exists(Path.Combine(folder.FullName, ".git")))
            {
                string env = Path.Combine(folder.FullName, ".env");
                string? line = File.Exists(env)
                    ? File.ReadAllLines(env).FirstOrDefault(l => l.Trim().StartsWith("VIFF_API_KEY=", StringComparison.Ordinal))
                    : null;
                return line is null ? string.Empty : Clean(line.Trim()["VIFF_API_KEY=".Length..]);
            }
        }

        return string.Empty;
    }

    private static string Clean(string key) => key.Trim().Replace("\"", string.Empty).Replace("'", string.Empty).Replace("\\", string.Empty);
}
