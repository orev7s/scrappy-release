using System.IO.Compression;
using System.Net.Http;

namespace Scrappy.Stream;

/// <summary>
/// Puts the FFmpeg libraries where the app can find them, without the user having to.
///
/// It deliberately does not download FFmpeg from ffmpeg.org or a third-party build: these bindings
/// are written against libavcodec 58 (FFmpeg 4.4), and pairing them with FFmpeg 6/7/8 would load a
/// library whose structs have a different layout. That does not fail cleanly - it corrupts memory.
/// So the only thing fetched is the exact set of DLLs this build was tested against, published as
/// a release asset by this project.
/// </summary>
public static class FFmpegInstaller
{
    /// <summary>
    /// "latest" rather than a version tag: the asset name is stable across releases, so installing
    /// FFmpeg does not depend on which Scrappy version happens to be installed.
    /// </summary>
    public const string DownloadUrl =
        "https://github.com/orev7s/scrappy-release/releases/latest/download/ffmpeg-win-x64.zip";

    /// <summary>Per-user, so no administrator rights are needed.</summary>
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Scrappy",
        "ffmpeg");

    /// <summary>
    /// Returns quietly if the libraries already load. Otherwise downloads and unpacks them, then
    /// checks again - so a true return value means they work, not that a download finished.
    /// </summary>
    public static async Task<bool> EnsureAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (AlreadyWorks())
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new DllNotFoundException(
                "FFmpeg libraries are missing and automatic installation is Windows-only. " +
                "Install your distribution's FFmpeg package, or set SCRAPPY_FFMPEG_DIR.");
        }

        progress?.Report("Downloading FFmpeg libraries (about 15 MB)");
        Directory.CreateDirectory(InstallDirectory);

        byte[] archive;
        using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            archive = await client.GetByteArrayAsync(DownloadUrl, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report("Unpacking");
        using (var stream = new MemoryStream(archive))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            var dlls = zip.Entries.Where(e => e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();

            // A wrong or truncated asset must stop here rather than produce a half-working install.
            if (!dlls.Any(e => e.Name.StartsWith("avcodec", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("The downloaded archive did not contain the FFmpeg libraries.");
            }

            foreach (ZipArchiveEntry entry in dlls)
            {
                string target = Path.Combine(InstallDirectory, entry.Name);
                entry.ExtractToFile(target, overwrite: true);
            }
        }

        FFmpegRuntime.Reset();

        if (!AlreadyWorks(out string? failure))
        {
            throw new DllNotFoundException($"FFmpeg was installed to {InstallDirectory} but still does not load: {failure}");
        }

        progress?.Report("FFmpeg ready");
        return true;
    }

    private static bool AlreadyWorks() => AlreadyWorks(out _);

    private static bool AlreadyWorks(out string? failure)
    {
        try
        {
            FFmpegRuntime.EnsureLocated();
            failure = null;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or DllNotFoundException)
        {
            failure = ex.Message;
            return false;
        }
    }
}
