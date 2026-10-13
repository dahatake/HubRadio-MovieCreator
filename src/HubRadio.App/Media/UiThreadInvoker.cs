using Microsoft.UI.Dispatching;

namespace HubRadio.App.Media;

/// <summary>
/// FR-061: the OS file/folder dialogs and <c>Window.AppWindow</c> must be used on the UI thread.
/// Bridge handlers can continue on a thread-pool thread (after <c>ConfigureAwait(false)</c>), so every
/// picker runs its work through this helper, which hops to the UI thread when needed.
/// </summary>
internal static class UiThreadInvoker
{
    internal static Task<T> RunAsync<T>(DispatcherQueue queue, Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return RunAsync(queue.HasThreadAccess, callback => queue.TryEnqueue(() => callback()), work);
    }

    /// <summary>Testable core: runs <paramref name="work"/> directly on the UI thread, otherwise enqueues it.</summary>
    internal static Task<T> RunAsync<T>(bool hasThreadAccess, Func<Action, bool> tryEnqueue, Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(tryEnqueue);
        ArgumentNullException.ThrowIfNull(work);

        if (hasThreadAccess)
        {
            return work();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = tryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await work());
            }
            catch (OperationCanceledException)
            {
                completion.SetCanceled();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        if (!queued)
        {
            completion.TrySetException(new InvalidOperationException("The UI thread is not available."));
        }

        return completion.Task;
    }
}
