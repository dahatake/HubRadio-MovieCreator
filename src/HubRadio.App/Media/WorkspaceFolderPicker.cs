using HubRadio.App.Web;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace HubRadio.App.Media;

/// <summary>FR-064: the OS folder dialog, owned by the app window. The path stays in the native host until it is returned.</summary>
internal sealed class WorkspaceFolderPicker : IWorkspaceFolderPicker
{
    private readonly Window owner;

    public WorkspaceFolderPicker(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        this.owner = owner;
    }

    public async Task<string?> PickFolderAsync(string? startFolder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FolderPicker picker = new(owner.AppWindow.Id);
        if (!string.IsNullOrEmpty(startFolder) && Directory.Exists(startFolder))
        {
            picker.SuggestedFolder = startFolder;
        }

        PickFolderResult? result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }
}
