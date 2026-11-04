namespace ResourceManager.ReadWrite.Windows.Processes.Etw;

public interface IEtwConsumerThreadObserver
{
    IDisposable RegisterCurrentThread(int blockId, string sessionName);

    void ReportFailure(int blockId, Exception error);
}
