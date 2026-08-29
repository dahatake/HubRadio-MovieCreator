using Azure.Core;
using Azure.Identity;

namespace HubRadio.Infrastructure.Azure;

public sealed class AzureCredentialProvider
{
    public const string AzureAiScope = "https://ai.azure.com/.default";

    private static readonly Uri RedirectUri = new("http://localhost");

    private readonly Func<TokenCredential> _credentialFactory;

    public AzureCredentialProvider(string tenantId, string clientId)
        : this(
            tenantId,
            clientId,
            () => new InteractiveBrowserCredential(
                CreateInteractiveBrowserOptions(tenantId, clientId)))
    {
    }

    internal AzureCredentialProvider(
        string tenantId,
        string clientId,
        Func<TokenCredential> credentialFactory)
    {
        TenantId = RequireCanonicalId(tenantId, nameof(tenantId));
        ClientId = RequireCanonicalId(clientId, nameof(clientId));
        ArgumentNullException.ThrowIfNull(credentialFactory);
        _credentialFactory = credentialFactory;
    }

    public string TenantId { get; }

    public string ClientId { get; }

    public AzureCredentialSession CreateSession()
    {
        TokenCredential credential = _credentialFactory()
            ?? throw new InvalidOperationException(
                "The Azure credential factory returned no credential.");
        return new AzureCredentialSession(credential);
    }

    internal static InteractiveBrowserCredentialOptions
        CreateInteractiveBrowserOptions(
            string tenantId,
            string clientId) =>
        new()
        {
            TenantId = RequireCanonicalId(tenantId, nameof(tenantId)),
            ClientId = RequireCanonicalId(clientId, nameof(clientId)),
            AuthorityHost = AzureAuthorityHosts.AzurePublicCloud,
            RedirectUri = RedirectUri,
        };

    private static string RequireCanonicalId(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!Guid.TryParseExact(value, "D", out Guid parsed) ||
            parsed == Guid.Empty ||
            !string.Equals(
                value,
                parsed.ToString("D"),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The Microsoft Entra identifier must be a canonical lowercase nonzero UUID.",
                parameterName);
        }

        return value;
    }
}

public sealed class AzureCredentialSession : TokenCredential
{
    private const string AuthenticationFailedMessage =
        "Azure authentication failed.";
    private const string SignedOutMessage =
        "The Azure authentication session is signed out.";

    private TokenCredential? _credential;

    internal AzureCredentialSession(TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        _credential = credential;
    }

    /// <summary>
    /// Gets a point-in-time view of whether this local session is signed out.
    /// Callers must still handle authentication failure from token requests.
    /// </summary>
    public bool IsSignedOut => Volatile.Read(ref _credential) is null;

    public void SignOut()
    {
        _ = Interlocked.Exchange(ref _credential, null);
    }

    public override AccessToken GetToken(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateScope(requestContext);
        TokenCredential credential = CaptureCredential();
        AccessToken token;
        try
        {
            token = credential.GetToken(
                requestContext,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AuthenticationFailedException)
        {
            throw new AuthenticationFailedException(AuthenticationFailedMessage);
        }

        EnsureSessionUnchanged(credential);
        return token;
    }

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateScope(requestContext);
        TokenCredential credential = CaptureCredential();
        AccessToken token;
        try
        {
            token = await credential.GetTokenAsync(
                    requestContext,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AuthenticationFailedException)
        {
            throw new AuthenticationFailedException(AuthenticationFailedMessage);
        }

        EnsureSessionUnchanged(credential);
        return token;
    }

    private TokenCredential CaptureCredential()
    {
        TokenCredential? credential = Volatile.Read(ref _credential);
        if (credential is null)
        {
            throw new AuthenticationFailedException(SignedOutMessage);
        }

        return credential;
    }

    private void EnsureSessionUnchanged(TokenCredential credential)
    {
        if (!ReferenceEquals(credential, Volatile.Read(ref _credential)))
        {
            throw new AuthenticationFailedException(SignedOutMessage);
        }
    }

    private static void ValidateScope(TokenRequestContext requestContext)
    {
        if (requestContext.Scopes is not { Length: 1 } scopes ||
            !string.Equals(
                scopes[0],
                AzureCredentialProvider.AzureAiScope,
                StringComparison.Ordinal))
        {
            throw new AuthenticationFailedException(
                "The Azure token scope is not allowed.");
        }
    }
}