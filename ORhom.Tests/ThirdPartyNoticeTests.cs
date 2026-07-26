using System.Text.Json;

namespace ORhom.Tests;

public sealed class ThirdPartyNoticeTests
{
    [Fact]
    public void EveryLockedRuntimePackageIsCoveredByThirdPartyNotices()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var lockDocument = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(
                repositoryRoot,
                "packages.lock.json")));
        var packageVersions = lockDocument.RootElement
            .GetProperty("dependencies")
            .EnumerateObject()
            .SelectMany(framework => framework.Value.EnumerateObject())
            .Select(package =>
                $"{package.Name} {package.Value.GetProperty("resolved").GetString()}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var notices = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "THIRD-PARTY-NOTICES.md"));

        var missingPackages = packageVersions
            .Where(packageVersion => !notices.Contains(
                packageVersion,
                StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            missingPackages.Length == 0,
            $"THIRD-PARTY-NOTICES.md does not cover exact versions: {string.Join(", ", missingPackages)}");
    }

    [Fact]
    public void SelfContainedRuntimeVersionsAndOfficialNoticesAreCovered()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var assetsDocument = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(
                repositoryRoot,
                "obj",
                "project.assets.json")));
        var downloadDependencies = assetsDocument.RootElement
            .GetProperty("project")
            .GetProperty("frameworks")
            .EnumerateObject()
            .SelectMany(framework =>
                framework.Value
                    .GetProperty("downloadDependencies")
                    .EnumerateArray()
                    .ToArray())
            .ToArray();
        var notices = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "THIRD-PARTY-NOTICES.md"));
        var releaseScript = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "scripts",
            "Build-WindowsRelease.ps1"));

        foreach (var packageName in new[]
                 {
                     "Microsoft.NETCore.App.Runtime.win-x64",
                     "Microsoft.WindowsDesktop.App.Runtime.win-x64"
                 })
        {
            var dependency = Assert.Single(
                downloadDependencies,
                dependency => dependency
                    .GetProperty("name")
                    .GetString() == packageName);
            var versionRange = dependency.GetProperty("version").GetString();
            Assert.NotNull(versionRange);
            var endpoints = versionRange
                .Trim('[', ']')
                .Split(',', StringSplitOptions.TrimEntries);

            Assert.Equal(2, endpoints.Length);
            Assert.Equal(endpoints[0], endpoints[1]);
            Assert.Contains(
                $"{packageName} {endpoints[0]}",
                notices,
                StringComparison.Ordinal);
            Assert.Contains(packageName, releaseScript, StringComparison.Ordinal);
        }

        Assert.Contains(
            "THIRD-PARTY-NOTICES.TXT",
            releaseScript,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ORhom.sln")) &&
                File.Exists(Path.Combine(
                    directory.FullName,
                    "packages.lock.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "The ORhom repository root could not be located.");
    }
}
