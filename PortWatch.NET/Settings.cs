using System.IO;

namespace PortWatch;

/// <summary>Where the live-traffic arrows are drawn. Never both: the other level stays clean.</summary>
public enum ArrowTarget
{
    /// <summary>Arrows beside each port number, and the number is colour coded.</summary>
    Port = 0,
    /// <summary>One arrow pair per process row.</summary>
    Process = 1,
}

/// <summary>
/// PortWatch's few settings, in a plain key=value file under %LOCALAPPDATA%\PortWatch (never beside the exe,
/// which <c>dotnet clean</c> wipes). Unknown or damaged values fall back to the defaults.
/// </summary>
internal sealed class Settings
{
    private const string ArrowsKey = "ArrowsOn";

    private readonly string _path;

    public ArrowTarget Arrows { get; set; } = ArrowTarget.Port;

    public Settings(string path) => _path = path;

    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(Logger.LogPath)!, "PortWatch.ini");

    public static Settings Load(string path)
    {
        var settings = new Settings(path);
        try
        {
            if (!File.Exists(path)) return settings;

            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
                if (key == ArrowsKey && Enum.TryParse<ArrowTarget>(value, ignoreCase: true, out var target)
                    && Enum.IsDefined(target))
                    settings.Arrows = target;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Could not read settings, using defaults: {ex.Message}");
        }
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, $"{ArrowsKey}={Arrows}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            Logger.Log($"Could not save settings: {ex.Message}");
        }
    }
}
