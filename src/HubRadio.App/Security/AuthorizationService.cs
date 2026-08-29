using HubRadio.Core.Security;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.Security;

public sealed class AuthorizationException : Exception
{
    internal AuthorizationException(
        AuthorizationOperation operation,
        AuthorizationRole? actingRole)
        : base(actingRole is null
            ? "The actor is not authorized to perform the requested operation."
            : "The actor is not authorized to perform the requested operation as the specified role.")
    {
        Operation = operation;
        ActingRole = actingRole;
    }

    public AuthorizationOperation Operation { get; }

    public AuthorizationRole? ActingRole { get; }
}

public sealed class AuthorizationService
{
    private readonly RoleAssignmentStore _roleAssignmentStore;

    public AuthorizationService(RoleAssignmentStore roleAssignmentStore)
    {
        ArgumentNullException.ThrowIfNull(roleAssignmentStore);
        _roleAssignmentStore = roleAssignmentStore;
    }

    public IReadOnlyList<AuthorizationRole> GetRoles(string actorSid) =>
        _roleAssignmentStore.GetRoles(actorSid);

    public bool CanPerform(
        string actorSid,
        AuthorizationOperation operation)
    {
        ValidateOperation(operation, nameof(operation));
        IReadOnlyList<AuthorizationRole> roles =
            _roleAssignmentStore.GetRoles(actorSid);
        return roles.Any(role => PermissionMatrix.CanPerform(role, operation));
    }

    public void Demand(
        string actorSid,
        AuthorizationOperation operation)
    {
        if (!CanPerform(actorSid, operation))
        {
            throw new AuthorizationException(operation, actingRole: null);
        }
    }

    public void DemandAs(
        string actorSid,
        AuthorizationRole actingRole,
        AuthorizationOperation operation)
    {
        ValidateRole(actingRole, nameof(actingRole));
        ValidateOperation(operation, nameof(operation));
        IReadOnlyList<AuthorizationRole> roles =
            _roleAssignmentStore.GetRoles(actorSid);
        if (!roles.Contains(actingRole) ||
            !PermissionMatrix.CanPerform(actingRole, operation))
        {
            throw new AuthorizationException(operation, actingRole);
        }
    }

    public void AssignRole(
        string actorSid,
        AuthorizationRole role,
        string assignedBySid,
        string reason)
    {
        ValidateRole(role, nameof(role));
        Demand(assignedBySid, AuthorizationOperation.ManageRoleAssignments);
        _roleAssignmentStore.Assign(actorSid, role, assignedBySid, reason);
    }

    public void RevokeRole(
        string actorSid,
        AuthorizationRole role,
        string revokedBySid)
    {
        ValidateRole(role, nameof(role));
        Demand(revokedBySid, AuthorizationOperation.ManageRoleAssignments);
        _roleAssignmentStore.Revoke(actorSid, role, revokedBySid);
    }

    public bool BootstrapInitialRoles(string currentActorSid) =>
        _roleAssignmentStore.BootstrapInitialRoles(currentActorSid);

    private static void ValidateRole(
        AuthorizationRole role,
        string parameterName)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                role,
                "Unknown authorization role.");
        }
    }

    private static void ValidateOperation(
        AuthorizationOperation operation,
        string parameterName)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                operation,
                "Unknown authorization operation.");
        }
    }
}