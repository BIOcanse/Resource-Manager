using ResourceManager.ReadWrite.Windows.Results.Explicit;

internal static class WindowsRawInputSessionConsumer
{
    internal static async Task RunRecordedAsync()
    {
        await WindowsRawInputCaptureSessionRecordedTests.RunAsync();
        Console.WriteLine("ReadWriteCore Raw Input consumer passed: explicit recorded Start/Stop, full packet retention, QPC fields, passive cache/collection, and failure atomicity.");
    }
}
