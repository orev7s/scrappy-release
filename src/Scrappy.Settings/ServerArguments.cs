using System.Text;

namespace Scrappy.Settings;

/// <summary>
/// Turns settings into the argument list the device-side server parses.
///
/// The server takes <c>key=value</c> pairs, not CLI flags: <c>--max-size</c> arrives as
/// <c>max_size=...</c>, and a bare flag arrives as <c>key=true</c> (upstream's client does the
/// same in <c>app/src/server.c</c>). Only options that are set are sent; everything else keeps
/// the server default, which is what the schema records.
/// </summary>
public static class ServerArguments
{
    public static IReadOnlyList<string> Build(SettingsStore settings)
    {
        var arguments = new List<string>(settings.Schema.Options.Count);

        foreach (ScrcpyOption option in settings.Schema.Options)
        {
            OptionSetting setting = settings.Get(option.Id);
            if (LockedOptions.Forced.TryGetValue(option.Id, out OptionSetting? forced))
            {
                setting = forced;
            }

            if (!setting.IsSet)
            {
                continue;
            }

            switch (option.Kind)
            {
                case OptionKind.Flag:
                    arguments.Add($"{option.ServerKey}=true");
                    break;

                case OptionKind.OptionalValue when string.IsNullOrEmpty(setting.Value):
                    arguments.Add(option.ServerKey);
                    break;

                default:
                    arguments.Add($"{option.ServerKey}={setting.Value}");
                    break;
            }
        }

        foreach (string raw in SplitRawArguments(settings.RawArguments))
        {
            arguments.Add(raw);
        }

        return arguments;
    }

    /// <summary>
    /// Splits the raw-arguments escape hatch on whitespace, keeping double-quoted runs together.
    /// Deliberately not a shell parser: no expansion, no globbing, no pipes.
    /// </summary>
    public static IReadOnlyList<string> SplitRawArguments(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        foreach (char c in raw)
        {
            switch (c)
            {
                case '"':
                    inQuotes = !inQuotes;
                    break;

                case ' ' or '\t' or '\r' or '\n' when !inQuotes:
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }

                    break;

                default:
                    current.Append(c);
                    break;
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    /// <summary>One-line preview, for the UI and for bug reports.</summary>
    public static string Preview(SettingsStore settings) => string.Join(' ', Build(settings));

    /// <summary>
    /// Formats the same set as CLI flags, which is what a human would type at the scrcpy prompt.
    /// Used by the settings window so a value can be checked against upstream documentation.
    /// </summary>
    public static string AsCliFlags(SettingsStore settings)
    {
        var parts = new List<string>();
        foreach (ScrcpyOption option in settings.Schema.Options)
        {
            OptionSetting setting = settings.Get(option.Id);
            if (LockedOptions.Forced.TryGetValue(option.Id, out OptionSetting? forced))
            {
                setting = forced;
            }

            if (!setting.IsSet)
            {
                continue;
            }

            parts.Add(option.Kind == OptionKind.Flag
                ? option.Flag
                : string.IsNullOrEmpty(setting.Value) ? option.Flag : $"{option.Flag}={setting.Value}");
        }

        return string.Join(' ', parts);
    }
}
