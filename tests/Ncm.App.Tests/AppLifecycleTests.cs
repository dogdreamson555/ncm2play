using Ncm.App;
using Xunit;

namespace Ncm.App.Tests;

public sealed class AppLifecycleTests
{
    [Fact]
    public void ExecutableEntryPointUsesSingleThreadedApartment()
    {
        var entryPoint = typeof(AppStorage).Assembly.EntryPoint;

        Assert.NotNull(entryPoint);
        Assert.True(entryPoint.IsDefined(typeof(STAThreadAttribute), inherit: false));
    }
}
