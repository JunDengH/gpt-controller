using System.Reflection;

namespace GptAccountManager.Infrastructure;

public static class ApplicationInfo
{
    private static readonly Lazy<string> VersionValue = new(ResolveVersion);

    public static string Version => VersionValue.Value;

    internal static string NormalizeVersion(
        string? informationalVersion,
        System.Version? assemblyVersion)
    {
        var normalized = informationalVersion?.Split('+', 2)[0].Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        if (assemblyVersion is null)
        {
            return "0.0.0";
        }

        if (assemblyVersion.Build < 0)
        {
            return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.0";
        }

        return assemblyVersion.Revision > 0
            ? assemblyVersion.ToString(4)
            : assemblyVersion.ToString(3);
    }

    private static string ResolveVersion()
    {
        var assembly = typeof(ApplicationInfo).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return NormalizeVersion(informationalVersion, assembly.GetName().Version);
    }
}
