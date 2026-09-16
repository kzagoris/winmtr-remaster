using System.Reflection;

namespace WinMtr.Core;

/// <summary>The shared application release version for reports and frontends.</summary>
public static class AppVersion
{
    /// <summary>Release version including any prerelease label, without build metadata.</summary>
    public static string Display { get; } = typeof(AppVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+')[0];
}
