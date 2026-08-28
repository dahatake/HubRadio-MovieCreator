using HubRadio.Core.Identity;
using HubRadio.Core.Provenance;
using HubRadio.Infrastructure.Media;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace HubRadio.App.Media;

internal sealed class MediaPicker
{
    private readonly Window owner;
    private readonly AssetCandidateRegistry candidateRegistry;

    public MediaPicker(Window owner, AssetCandidateRegistry candidateRegistry)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(candidateRegistry);

        this.owner = owner;
        this.candidateRegistry = candidateRegistry;
    }

    public async Task<EntityId<AssetIdTag>?> PickAssetCandidateAsync()
    {
        FileOpenPicker picker = new(owner.AppWindow.Id);
        PickFileResult? result = await picker.PickSingleFileAsync();

        // The selected path remains in the native host. Only its opaque candidate ID
        // is returned to the caller that will eventually cross the WebView boundary.
        return candidateRegistry.RegisterSelection(result?.Path);
    }
}