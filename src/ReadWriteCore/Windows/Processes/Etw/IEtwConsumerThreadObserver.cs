namespace ResourceManager.ReadWrite.Windows.Processes.Etw;

public interface IEtwConsumerThreadObserver
{
    IDisposable RegisterCurrentThread(string blockId, string sessionName);

    void ReportFailure(string blockId, Exception error);
}
