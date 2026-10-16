using System.Reflection;

namespace EmailRelayAPI.Services;

public static class AppInfo
{
    public static readonly DateTime StartedAt = DateTime.UtcNow;

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "1.0.0";
}
