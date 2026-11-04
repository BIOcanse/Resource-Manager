using ResourceManager.Shared.Localization;

namespace ResourceManager.NativeUi;

/// <summary>The compatibility entry reports failures through the same localized launch error as Start.exe.</summary>
internal static class ResidentHandoffRunner
{
    internal static int Run(Func<ResidentHandoffOutcome> handoff, ToolText text, Action<string> showFailure)
    {
        try
        {
            if (handoff() != ResidentHandoffOutcome.Unanswered) return 0;
            showFailure(text.Format(text.LauncherFailedFormat, text.LauncherUiStartFailed));
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or InvalidOperationException
            or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("The resident could not be started: {0}", failure);
            showFailure(text.Format(text.LauncherFailedFormat, failure.Message));
        }
        return 2;
    }
}
