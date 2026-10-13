using HubRadio.App.Web;
using HubRadio.Infrastructure.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace HubRadio.App.Media;

/// <summary>FR-035.14/FR-035.17 native dialogs. Selected paths stay in the native host.</summary>
internal sealed class BookendStitchFilePicker : IBookendStitchFilePicker
{
    private readonly Window owner;

    public BookendStitchFilePicker(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        this.owner = owner;
    }

    public async Task<string?> PickClipAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await UiThreadInvoker.RunAsync(owner.DispatcherQueue, async () =>
        {
            FileOpenPicker picker = new(owner.AppWindow.Id);
            foreach (string extension in BookendStitcher.AllowedInputExtensions)
                picker.FileTypeFilter.Add(extension);
            PickFileResult? result = await picker.PickSingleFileAsync();
            return result?.Path;
        });
    }

    public async Task<string?> PickOutputAsync(string suggestedFileName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await UiThreadInvoker.RunAsync(owner.DispatcherQueue, async () =>
        {
            FileSavePicker picker = new(owner.AppWindow.Id)
            {
                SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedFileName),
                DefaultFileExtension = ".mp4",
            };
            picker.FileTypeChoices.Add("MP4 動画", [".mp4"]);
            PickFileResult? result = await picker.PickSaveFileAsync();
            return result?.Path;
        });
    }
}
