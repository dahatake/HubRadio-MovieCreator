namespace HubRadio.App.Web;

internal sealed class MainWindowStartupLifecycle
{
    private bool isStarted;
    private bool isClosed;

    internal bool CanUpdateUi => !isClosed;

    internal bool TryStart()
    {
        if (isStarted || isClosed)
        {
            return false;
        }

        isStarted = true;
        return true;
    }

    internal void Close()
    {
        isClosed = true;
    }
}
