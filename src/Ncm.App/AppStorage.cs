using System.Text;

namespace Ncm.App;

public static class AppStorage
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NcmConverter");

    public static string LogPath => Path.Combine(DataDirectory, "startup.log");

    public static void Initialize() => Initialize(DataDirectory);

    internal static void Initialize(string dataDirectory)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var logPath = Path.Combine(dataDirectory, "startup.log");
            File.AppendAllText(logPath, $"{DateTimeOffset.UtcNow:O} started{Environment.NewLine}", Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }
}
