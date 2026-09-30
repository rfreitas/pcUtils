using System.IO;
using Shared;
using Xunit;

public class StartupTaskTests
{
    [Fact]
    public void Install_then_uninstall_round_trips_a_non_elevated_logon_task()
    {
        // Real Task Scheduler round trip under a throwaway name; always cleaned up.
        string name = "PortWatch_Test_" + Guid.NewGuid().ToString("N");
        string exe = Path.Combine(AppContext.BaseDirectory, "PortWatch.NET.Tests.dll");
        try
        {
            Assert.False(StartupTaskService.IsInstalled(name));
            Assert.True(StartupTaskService.Install(name, exe, "test", requireElevation: false));
            Assert.True(StartupTaskService.IsInstalled(name));
            Assert.True(StartupTaskService.Uninstall(name));
            Assert.False(StartupTaskService.IsInstalled(name));
        }
        finally { StartupTaskService.Uninstall(name); }
    }
}
