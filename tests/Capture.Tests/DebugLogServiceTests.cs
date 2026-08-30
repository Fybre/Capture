using System.Diagnostics;
using Capture.Core.Diagnostics;
using Capture.Core.Paths;

namespace Capture.Tests;

public class DebugLogServiceTests
{
    [Fact]
    public void Writes_trace_output_to_disk_only_while_enabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-debug-log-test-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var service = new DebugLogService(paths);

        try
        {
            Assert.False(service.IsEnabled);
            Assert.False(File.Exists(service.LogFilePath));

            service.SetEnabled(true);
            Assert.True(service.IsEnabled);
            Trace.TraceInformation("marker-while-enabled");

            var contentsWhileEnabled = File.ReadAllText(service.LogFilePath);
            Assert.Contains("marker-while-enabled", contentsWhileEnabled);

            service.SetEnabled(false);
            Assert.False(service.IsEnabled);

            var lengthAfterDisable = new FileInfo(service.LogFilePath).Length;
            Trace.TraceInformation("marker-after-disabled");
            Assert.Equal(lengthAfterDisable, new FileInfo(service.LogFilePath).Length);
        }
        finally
        {
            service.SetEnabled(false);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
