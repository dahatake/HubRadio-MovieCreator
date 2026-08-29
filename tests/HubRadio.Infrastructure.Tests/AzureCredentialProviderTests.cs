using System.Reflection;
using Azure.Core;
using Azure.Identity;
using HubRadio.Infrastructure.Azure;

namespace HubRadio.Infrastructure.Tests;

[TestClass]
public sealed class AzureCredentialProviderTests
{
    private const string TenantId = "16b3c013-d300-468d-ac64-7eda0820b6d3";
    private const string ClientId = "f1088a3a-7414-4afc-bd9f-a845bde4d1f4";

    private static readonly TokenRequestContext AllowedRequest =
        new([AzureCredentialProvider.AzureAiScope]);

    [TestMethod]
    public void InteractiveOptions_AreSingleTenantPublicClientAndDoNotPersistTokens()
    {
        InteractiveBrowserCredentialOptions options =
            AzureCredentialProvider.CreateInteractiveBrowserOptions(
                TenantId,
                ClientId);

        Assert.AreEqual(TenantId, options.TenantId);
        Assert.AreEqual(ClientId, options.ClientId);
        Assert.AreEqual(AzureAuthorityHosts.AzurePublicCloud, options.AuthorityHost);
        Assert.AreEqual(new Uri("http://localhost"), options.RedirectUri);
        Assert.IsNull(options.TokenCachePersistenceOptions);
    }

    [TestMethod]
    public async Task Session_AllowsOnlyAzureAiScopeAndSignsOutWithoutReturningLateToken()
    {
        var pending = new TaskCompletionSource<AccessToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var credential = new RecordingCredential(
            (_, _) => new ValueTask<AccessToken>(pending.Task));
        var provider = new AzureCredentialProvider(
            TenantId,
            ClientId,
            () => credential);
        AzureCredentialSession session = provider.CreateSession();

        Task<AccessToken> request = session
            .GetTokenAsync(AllowedRequest, CancellationToken.None)
            .AsTask();
        session.SignOut();
        pending.SetResult(new AccessToken(
            "private-late-token",
            DateTimeOffset.UtcNow.AddMinutes(5)));

        AuthenticationFailedException late =
            await Assert.ThrowsExactlyAsync<AuthenticationFailedException>(
                () => request);
        Assert.AreEqual(
            "The Azure authentication session is signed out.",
            late.Message);
        Assert.IsTrue(session.IsSignedOut);
        Assert.AreEqual(1, credential.AsyncCallCount);

        foreach (TokenRequestContext disallowed in new[]
        {
            default,
            new TokenRequestContext([]),
            new TokenRequestContext(["https://management.azure.com/.default"]),
            new TokenRequestContext([
                AzureCredentialProvider.AzureAiScope,
                "https://management.azure.com/.default",
            ]),
        })
        {
            AuthenticationFailedException scope =
                await Assert.ThrowsExactlyAsync<AuthenticationFailedException>(() =>
                    session.GetTokenAsync(disallowed, CancellationToken.None).AsTask());
            Assert.AreEqual("The Azure token scope is not allowed.", scope.Message);
        }
    }

    [TestMethod]
    public void Session_SynchronousPathReturnsTokenAndSanitizesRevocation()
    {
        var successCredential = new RecordingCredential(
            (_, _) => new ValueTask<AccessToken>(new AccessToken(
                "synthetic-sync-token",
                DateTimeOffset.UtcNow.AddMinutes(5))));
        AzureCredentialSession successSession = new AzureCredentialProvider(
                TenantId,
                ClientId,
                () => successCredential)
            .CreateSession();

        AccessToken token = successSession.GetToken(
            AllowedRequest,
            CancellationToken.None);

        Assert.AreEqual("synthetic-sync-token", token.Token);
        Assert.AreEqual(1, successCredential.SyncCallCount);
        Assert.AreEqual(0, successCredential.AsyncCallCount);

        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            successSession.GetToken(AllowedRequest, alreadyCancelled.Token));
        Assert.AreEqual(1, successCredential.SyncCallCount);

        const string privateFailure = "AADSTS private synchronous detail";
        var failedCredential = new RecordingCredential(
            (_, _) => throw new AuthenticationFailedException(privateFailure));
        AzureCredentialSession failedSession = new AzureCredentialProvider(
                TenantId,
                ClientId,
                () => failedCredential)
            .CreateSession();

        AuthenticationFailedException exception =
            Assert.ThrowsExactly<AuthenticationFailedException>(() =>
                failedSession.GetToken(AllowedRequest, CancellationToken.None));
        Assert.AreEqual("Azure authentication failed.", exception.Message);
        Assert.IsFalse(exception.ToString().Contains(
            privateFailure,
            StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Session_PropagatesCancellationWithoutCallingOrSigningOut()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var credential = new RecordingCredential(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after cancellation.");
        });
        AzureCredentialSession session = new AzureCredentialProvider(
                TenantId,
                ClientId,
                () => credential)
            .CreateSession();
        using var cancellation = new CancellationTokenSource();

        Task<AccessToken> request = session
            .GetTokenAsync(AllowedRequest, cancellation.Token)
            .AsTask();
        await entered.Task;
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => request);
        Assert.IsFalse(session.IsSignedOut);
        Assert.AreEqual(1, credential.AsyncCallCount);

        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            session.GetTokenAsync(AllowedRequest, alreadyCancelled.Token).AsTask());
        Assert.AreEqual(1, credential.AsyncCallCount);
    }

    [TestMethod]
    public async Task Session_ConcurrentLateTokensAreAllRejectedAfterIdempotentSignOut()
    {
        var pending = new TaskCompletionSource<AccessToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var credential = new RecordingCredential(
            (_, _) => new ValueTask<AccessToken>(pending.Task));
        AzureCredentialSession session = new AzureCredentialProvider(
                TenantId,
                ClientId,
                () => credential)
            .CreateSession();
        Task<AccessToken>[] requests = Enumerable.Range(0, 16)
            .Select(_ => session
                .GetTokenAsync(AllowedRequest, CancellationToken.None)
                .AsTask())
            .ToArray();
        Assert.AreEqual(16, credential.AsyncCallCount);

        session.SignOut();
        session.SignOut();
        pending.SetResult(new AccessToken(
            "synthetic-concurrent-token",
            DateTimeOffset.UtcNow.AddMinutes(5)));

        foreach (Task<AccessToken> request in requests)
        {
            AuthenticationFailedException exception =
                await Assert.ThrowsExactlyAsync<AuthenticationFailedException>(
                    () => request);
            Assert.AreEqual(
                "The Azure authentication session is signed out.",
                exception.Message);
        }
    }

    [TestMethod]
    public async Task Session_MapsRevokedCredentialFailureWithoutLeakingDetails()
    {
        const string privateFailure =
            "AADSTS private tenant and account detail";
        var credential = new RecordingCredential(
            (_, _) => throw new AuthenticationFailedException(privateFailure));
        var provider = new AzureCredentialProvider(
            TenantId,
            ClientId,
            () => credential);

        AuthenticationFailedException exception =
            await Assert.ThrowsExactlyAsync<AuthenticationFailedException>(() =>
                provider.CreateSession()
                    .GetTokenAsync(AllowedRequest, CancellationToken.None)
                    .AsTask());

        Assert.AreEqual("Azure authentication failed.", exception.Message);
        Assert.IsNull(exception.InnerException);
        Assert.IsFalse(exception.ToString().Contains(
            privateFailure,
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void Provider_RejectsInvalidIdsNullFactoryAndNullCredential()
    {
        foreach (string invalid in new[]
        {
            string.Empty,
            "not-a-guid",
            "00000000-0000-0000-0000-000000000000",
            TenantId.ToUpperInvariant(),
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                new AzureCredentialProvider(invalid, ClientId));
            Assert.ThrowsExactly<ArgumentException>(() =>
                new AzureCredentialProvider(TenantId, invalid));
        }

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new AzureCredentialProvider(TenantId, ClientId, null!));
        var provider = new AzureCredentialProvider(
            TenantId,
            ClientId,
            () => null!);
        Assert.ThrowsExactly<InvalidOperationException>(provider.CreateSession);
    }

    [TestMethod]
    public void PublicSurface_IsTypedImmutableAndHasNoSecretOrPathProperties()
    {
        foreach (Type type in new[]
        {
            typeof(AzureCredentialProvider),
            typeof(AzureCredentialSession),
        })
        {
            Assert.IsTrue(type.IsPublic, type.Name);
            Assert.IsTrue(type.IsSealed, type.Name);
            Assert.IsFalse(type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Any(static property =>
                    property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)));
        }

        CollectionAssert.AreEqual(
            new[] { "CreateSession" },
            typeof(AzureCredentialProvider)
                .GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { "SignOut", "GetToken", "GetTokenAsync" },
            typeof(AzureCredentialSession)
                .GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName)
                .Select(static method => method.Name)
                .ToArray());
    }

    private sealed class RecordingCredential(
        Func<TokenRequestContext, CancellationToken, ValueTask<AccessToken>> getTokenAsync)
        : TokenCredential
    {
        private int _asyncCallCount;
        private int _syncCallCount;

        internal int AsyncCallCount => Volatile.Read(ref _asyncCallCount);

        internal int SyncCallCount => Volatile.Read(ref _syncCallCount);

        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _syncCallCount);
            return getTokenAsync(requestContext, cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _asyncCallCount);
            return getTokenAsync(requestContext, cancellationToken);
        }
    }
}