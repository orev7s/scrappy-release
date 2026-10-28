using FFmpeg.AutoGen;

namespace Scrappy.Stream;

/// <summary>
/// Finds the FFmpeg shared libraries and tells FFmpeg.AutoGen where they are.
///
/// The build output layout differs between a package reference (libraries land under
/// <c>runtimes/&lt;rid&gt;/native</c>) and a published app (libraries sit next to the executable),
/// so this probes both rather than assuming one.
/// </summary>
public static class FFmpegRuntime
{
    private static readonly object Gate = new();
    private static bool _located;
    private static string? _failure;

    /// <summary>The directory the libraries were loaded from, or null before <see cref="EnsureLocated"/>.</summary>
    public static string? LibraryDirectory { get; private set; }

    /// <summary>Every directory that was tried, in order. Useful when loading fails on a new machine.</summary>
    public static IReadOnlyList<string> ProbedDirectories { get; private set; } = [];

    public static string Version { get; private set; } = "unknown";

    /// <summary>Forgets a previous failure so the next call probes again, after an install.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _located = false;
            _failure = null;
            LibraryDirectory = null;
        }
    }

    public static void EnsureLocated()
    {
        lock (Gate)
        {
            if (_located)
            {
                return;
            }

            var probed = new List<string>();
            string baseDirectory = AppContext.BaseDirectory;

            foreach (string candidate in Candidates(baseDirectory))
            {
                probed.Add(candidate);
                if (!Directory.Exists(candidate))
                {
                    continue;
                }

                if (!ContainsFFmpeg(candidate))
                {
                    continue;
                }

                ffmpeg.RootPath = candidate;
                LibraryDirectory = candidate;
                RegisterWithLoader(candidate);
                ProbedDirectories = probed;

                try
                {
                    Version = $"avcodec {ffmpeg.avcodec_version()}";
                }
                catch (DllNotFoundException ex)
                {
                    _failure = $"Found libraries in '{candidate}' but could not load them: {ex.Message}";
                    continue;
                }

                _located = true;
                _failure = null;
                return;
            }

            ProbedDirectories = probed;
            _failure =
                "FFmpeg shared libraries were not found. They ship next to Scrappy.App.exe in the " +
                "release zip - if the executable was moved, move the whole folder. Alternatively set " +
                "SCRAPPY_FFMPEG_DIR to the folder holding avcodec. Probed: " +
                string.Join(", ", probed);
            throw new DllNotFoundException(_failure);
        }
    }

    /// <summary>
    /// Windows resolves a dependency of a DLL from the application directory, the system
    /// directories and PATH. When the libraries are somewhere else - a dev build's runtimes
    /// folder, or a directory named by SCRAPPY_FFMPEG_DIR - that search has to be widened first,
    /// or avcodec loads and then fails looking for libgcc_s_seh-1.dll.
    /// </summary>
    private static void RegisterWithLoader(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            SetDllDirectory(directory);
        }
        catch (EntryPointNotFoundException)
        {
            // Very old Windows. The application-directory layout still works.
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string pathName);

    private static IEnumerable<string> Candidates(string baseDirectory)
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("SCRAPPY_FFMPEG_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            yield return fromEnvironment;
        }

        yield return baseDirectory;

        string architecture = Environment.Is64BitProcess ? "x64" : "x86";
        string[] platformPrefixes = OperatingSystem.IsWindows()
            ? ["win", "win7", "win10"]
            : OperatingSystem.IsMacOS() ? ["osx"] : ["linux"];

        foreach (string prefix in platformPrefixes)
        {
            yield return Path.Combine(baseDirectory, "runtimes", $"{prefix}-{architecture}", "native");
        }

        yield return Path.Combine(baseDirectory, "native");

        // Where FFmpegInstaller puts a downloaded copy.
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            yield return Path.Combine(local, "Scrappy", "ffmpeg");
        }
    }

    private static bool ContainsFFmpeg(string directory)
    {
        try
        {
            // Libtool names differ per platform: libavcodec.so.58, avcodec-58.dll, libavcodec.58.dylib.
            return Directory.EnumerateFiles(directory)
                .Select(Path.GetFileName)
                .Any(name => name is not null && name.Contains("avcodec", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return false;
        }
    }
}
