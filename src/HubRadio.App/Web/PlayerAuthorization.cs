using HubRadio.App.Security;
using HubRadio.Core.Security;
using HubRadio.Infrastructure.Identity;

namespace HubRadio.App.Web;

internal interface IPlayerAccessAuthorization
{
    void DemandPreview();
}

internal sealed class PlayerAccessAuthorization : IPlayerAccessAuthorization
{
    private readonly AuthorizationService authorizationService;
    private readonly Func<string> getCurrentSid;

    internal PlayerAccessAuthorization(
        AuthorizationService authorizationService,
        WindowsActorProvider actorProvider)
        : this(authorizationService, GetActorSidProvider(actorProvider))
    {
    }

    internal PlayerAccessAuthorization(
        AuthorizationService authorizationService,
        Func<string> getCurrentSid)
    {
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(getCurrentSid);
        this.authorizationService = authorizationService;
        this.getCurrentSid = getCurrentSid;
    }

    public void DemandPreview()
    {
        authorizationService.Demand(
            getCurrentSid(),
            AuthorizationOperation.PreviewProject);
    }

    private static Func<string> GetActorSidProvider(
        WindowsActorProvider actorProvider)
    {
        ArgumentNullException.ThrowIfNull(actorProvider);
        return actorProvider.GetCurrentSid;
    }
}

internal sealed class DeniedPlayerAccessAuthorization :
    IPlayerAccessAuthorization
{
    internal static DeniedPlayerAccessAuthorization Instance { get; } = new();

    private DeniedPlayerAccessAuthorization()
    {
    }

    public void DemandPreview()
    {
        throw new AuthorizationException(
            AuthorizationOperation.PreviewProject,
            actingRole: null);
    }
}