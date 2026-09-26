namespace ResourceManager.App.Infrastructure.Monitoring.AmdSmu;

internal sealed partial class AmdSmuPawnIoSession
{
    private interface IAmdSmuPawnIoExecutor : IDisposable
    {
        string Name { get; }

        ulong[] Execute(string functionName, ulong[] input, int outputLength);
    }

    private static uint ReadOptionalU32(IAmdSmuPawnIoExecutor executor, string functionName)
    {
        try
        {
            var output = executor.Execute(functionName, [], 1);
            return output.Length > 0 ? (uint)(output[0] & 0xFFFFFFFFUL) : 0;
        }
        catch (AmdSmuProviderUnavailableException)
        {
            return 0;
        }
    }

    private static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult == 0)
        {
            return;
        }

        throw new AmdSmuProviderUnavailableException(
            $"{operation} 失败：0x{unchecked((uint)hresult):X8}");
    }

    private static uint HResultFromWin32(int error)
    {
        return error <= 0 ? unchecked((uint)error) : (uint)(0x80070000 | error);
    }
}

internal sealed class AmdSmuProviderUnavailableException(string message) : Exception(message);
