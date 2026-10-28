using System.Reflection;

namespace Scrappy.Update;

/// <summary>
/// The version of the running program, which is what a release is compared against.
///
/// Read from the entry assembly's informational version (the csproj <c>&lt;Version&gt;</c>), so
/// there is one place a release number is written: the project. A build that carries no version at
/// all reads as 0.0.0, which compares below every release and would offer an update - the safe
/// direction to be wrong in, and impossible in a build that ships.
/// </summary>
public static class AppVersion
{
    public static SemanticVersion Current { get; } = Read();

    /// <summary>The same thing as text, for a log line or a title.</summary>
    public static string Text => Current.ToString();

    private static SemanticVersion Read()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        // "0.2.1+9c1f0a2" is what SourceLink produces; the part after '+' is not a version.
        if (SemanticVersion.Parse(informational) is { } fromInformational)
        {
            return fromInformational;
        }

        string? file = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (SemanticVersion.Parse(file) is { } fromFile)
        {
            return fromFile;
        }

        // 0.0.0, not 1.0.0: an assembly with no version must not claim to be ahead of every
        // release. It compares below everything, so it would be offered an update.
        return SemanticVersion.Parse(assembly.GetName().Version?.ToString()) ?? SemanticVersion.Zero;
    }
}
