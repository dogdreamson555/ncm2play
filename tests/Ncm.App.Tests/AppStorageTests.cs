using Ncm.App;
using Xunit;

namespace Ncm.App.Tests;

public sealed class AppStorageTests
{
    [Fact]
    public void StartupAppendsLogWithoutCreatingSettings()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            AppStorage.Initialize(directory.FullName);
            AppStorage.Initialize(directory.FullName);

            var lines = File.ReadAllLines(Path.Combine(directory.FullName, "startup.log"));
            Assert.Equal(2, lines.Length);
            Assert.All(lines, line => Assert.EndsWith(" started", line, StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(directory.FullName, "settings.json")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void UnavailableStorageDoesNotPreventStartup()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var occupiedPath = Path.Combine(directory.FullName, "occupied");
            File.WriteAllText(occupiedPath, "keep");

            AppStorage.Initialize(occupiedPath);

            Assert.Equal("keep", File.ReadAllText(occupiedPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void UnwritableLogDoesNotPreventStartupOrOverwriteSettings()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var settingsPath = Path.Combine(directory.FullName, "settings.json");
            File.WriteAllText(settingsPath, "{\"schemaVersion\":1,\"keep\":true}");
            Directory.CreateDirectory(Path.Combine(directory.FullName, "startup.log"));

            AppStorage.Initialize(directory.FullName);

            Assert.Equal("{\"schemaVersion\":1,\"keep\":true}", File.ReadAllText(settingsPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void StorageFilesAreOutsideApplicationDirectory()
    {
        var applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var dataDirectory = Path.GetFullPath(AppStorage.DataDirectory);

        Assert.False(dataDirectory.StartsWith(applicationDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith(dataDirectory, AppStorage.LogPath, StringComparison.OrdinalIgnoreCase);
    }
}
