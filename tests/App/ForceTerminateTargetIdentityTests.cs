using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ResourceManager.NativeUi.SystemIntegration.EditableHotkeys;

namespace Resource_Manager_APP.Tests;

public sealed class ForceTerminateTargetIdentityTests
{
    [Fact]
    public void CapturedTargetCarriesExactCreationTimeInCurrentWireShape()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true });
        Assert.NotNull(child);
        try
        {
            var target = HungAndForegroundProcessResolver.ReadAllowedTarget(child.Id);
            Assert.NotNull(target);
            Assert.Equal(child.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture), target.ProcessStartKey);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { targets = new[] { target } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Equal(child.Id, json.RootElement.GetProperty("targets")[0].GetProperty("processId").GetInt32());
            Assert.False(json.RootElement.TryGetProperty("processIds", out _));
        }
        finally { if (!child.HasExited) { child.StandardInput.WriteLine(); child.StandardInput.Close(); child.WaitForExit(5000); } }
    }

    [Fact]
    public void MissingAndSelfTargetsAreExcluded()
    {
        Assert.Null(HungAndForegroundProcessResolver.ReadAllowedTarget(int.MaxValue));
        Assert.Null(HungAndForegroundProcessResolver.ReadAllowedTarget(Environment.ProcessId));
    }
}
