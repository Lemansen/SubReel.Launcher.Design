using System.Reflection;
using SubReel.Launcher.Application.Updates;

namespace SubReel.Launcher.Infrastructure.Updates;

/// <summary>Версия запущенного лаунчера: <c>AssemblyInformationalVersion</c> (из &lt;Version&gt; в csproj) без суффиксов «-beta» и «+коммит».</summary>
public sealed class AssemblyAppVersion : IAppVersion
{
    public AssemblyAppVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Current = UpdateRules.ParseVersion(informational)
                  ?? UpdateRules.ParseVersion(assembly?.GetName().Version?.ToString())
                  ?? new Version(0, 0, 0);
    }

    public Version Current { get; }
}
