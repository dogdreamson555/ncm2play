using System.Text;

namespace Ncm.App;

public static class AppStorage
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NcmConverter");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static string LogPath => Path.Combine(DataDirectory, "startup.log");

    public static void Initialize() => Initialize(DataDirectory);

    internal static void Initialize(string dataDirectory)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var settingsPath = Path.Combine(dataDirectory, "settings.json");
            try
            {
                using var stream = new FileStream(settingsPath, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write("{\"schemaVersion\":1}");
            }
            catch (IOException) when (File.Exists(settingsPath))
            {
            }

            var logPath = Path.Combine(dataDirectory, "startup.log");
            File.AppendAllText(logPath, $"{DateTimeOffset.UtcNow:O} started{Environment.NewLine}", Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }
}
