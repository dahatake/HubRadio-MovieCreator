using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HubRadio.App.Projects;
using HubRadio.App.Security;
using HubRadio.Core.Identity;
using HubRadio.Core.Projects;
using HubRadio.Core.Security;
using HubRadio.Infrastructure.Database;
using HubRadio.Infrastructure.Files;

namespace HubRadio.App.Web;

internal sealed class SourceOnlyMediaAssetReference
{
    internal const string Prefix = "https://" + WebViewHost.MediaHostName + "/source-only/";
    internal static int CanonicalUriLength => Prefix.Length + (3 * 36) + 3 + 64;

    internal SourceOnlyMediaAssetReference(EntityId<ProjectIdTag> projectId,
        EntityId<SourceOnlyExecutionIdTag> executionId, EntityId<ArtifactIdTag> artifactId, ContentDigest contentDigest)
    {
        ProjectId = projectId ?? throw new ArgumentNullException(nameof(projectId));
        ExecutionId = executionId ?? throw new ArgumentNullException(nameof(executionId));
        ArtifactId = artifactId ?? throw new ArgumentNullException(nameof(artifactId));
        ContentDigest = contentDigest ?? throw new ArgumentNullException(nameof(contentDigest));
        Value = $"{Prefix}{projectId}/{executionId}/{artifactId}/{contentDigest.Hex}";
    }

    internal EntityId<ProjectIdTag> ProjectId { get; }
    internal EntityId<SourceOnlyExecutionIdTag> ExecutionId { get; }
    internal EntityId<ArtifactIdTag> ArtifactId { get; }
    internal ContentDigest ContentDigest { get; }
    internal string Value { get; }

    internal static bool TryParse(string? value, [NotNullWhen(true)] out SourceOnlyMediaAssetReference? reference)
    {
        reference = null;
        // Raw ordinal comparison deliberately rejects URI normalization, escapes,
        // dot segments, user info, ports, query, fragment, extra slashes and whitespace.
        if (value is null || value.Length != CanonicalUriLength || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = value[Prefix.Length..].Split('/');
        if (parts.Length != 4 ||
            !TryId(parts[0], out EntityId<ProjectIdTag>? project) ||
            !TryId(parts[1], out EntityId<SourceOnlyExecutionIdTag>? execution) ||
            !TryId(parts[2], out EntityId<ArtifactIdTag>? artifact) ||
            !ContentDigest.TryParse(parts[3], out ContentDigest? digest) || digest.Hex != parts[3])
        {
            return false;
        }

        reference = new(project, execution, artifact, digest);
        return string.Equals(value, reference.Value, StringComparison.Ordinal);
    }

    // Routing is broader than validation: even a malformed source-only request must
    // never fall through to the legacy player reader or a network fetch.
    internal static bool IsSourceOnlyRequest(string? value) =>
        value?.StartsWith(Prefix, StringComparison.Ordinal) == true ||
        (WebViewHost.IsAllowedMediaOrigin(value) && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
            (uri.AbsolutePath.Equals("/source-only", StringComparison.Ordinal) ||
                uri.AbsolutePath.StartsWith("/source-only/", StringComparison.Ordinal)));

    private static bool TryId<T>(string text, [NotNullWhen(true)] out EntityId<T>? id) =>
        EntityId<T>.TryParse(text, out id) && id.ToString() == text;
}

/// <summary>
/// Generated-media GET boundary. The host supplies real services for one workspace:
/// SourceOnlyMediaResponseFactory(SourceOnlyExecutionStore executionStore,
/// SourceOnlyResultService resultService, ArtifactStore artifactStore,
/// Func&lt;string&gt; currentActorSid, AuthorizationService authorization).
/// Only outputs explicitly listed in the requested stored execution's validated result
/// are readable. A former execution remains readable during regeneration, but there is
/// no lookup by current working copy, source/evidence/delivery ID, file path or URL.
/// Validation is not publication/delivery approval; it authorizes no workflow transition.
/// </summary>
internal sealed class SourceOnlyMediaResponseFactory
{
    internal const int MaximumRangeHeaderCharacters = 128;
    private readonly SourceOnlyExecutionStore executionStore;
    private readonly SourceOnlyResultService resultService;
    private readonly ArtifactStore artifactStore;
    private readonly Func<string> currentActorSid;
    private readonly AuthorizationService authorization;

    internal SourceOnlyMediaResponseFactory(SourceOnlyExecutionStore executionStore,
        SourceOnlyResultService resultService, ArtifactStore artifactStore,
        Func<string> currentActorSid, AuthorizationService authorization)
    {
        this.executionStore = executionStore ?? throw new ArgumentNullException(nameof(executionStore));
        this.resultService = resultService ?? throw new ArgumentNullException(nameof(resultService));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.currentActorSid = currentActorSid ?? throw new ArgumentNullException(nameof(currentActorSid));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
    }

    /// <summary>FR-053: poster frames of listed Projects. Null serves no poster (404).</summary>
    internal ProjectPosterStore? Posters { get; init; }

    /// <summary>FR-053: a poster is served only while its Project (draft) is listed, i.e. not deleted.</summary>
    internal ProjectCreationDraftStore? Drafts { get; init; }

    internal const string PosterPrefix = SourceOnlyMediaAssetReference.Prefix + "poster/";

    /// <summary>FR-144: the source-video proxies of listed Projects. Null serves no proxy (404).</summary>
    internal ProjectProxyService? Proxies { get; init; }

    internal const string ProxyPrefix = SourceOnlyMediaAssetReference.Prefix + "proxy/";

    internal static string ProxyUri(EntityId<ProjectCreationDraftIdTag> draftId, string version) =>
        $"{ProxyPrefix}{draftId}/{version}";

    internal static string PosterUri(EntityId<ProjectCreationDraftIdTag> draftId, ContentDigest digest) =>
        $"{PosterPrefix}{draftId}/{digest.Hex}";

    internal async Task<PlayerMediaResponse> CreateAsync(string? method, string? uri, string? rangeHeader,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(method, "GET", StringComparison.Ordinal))
        {
            return Empty(405, "Method Not Allowed", allowGet: true);
        }

        ValidatedArtifactReadStream? held = null;
        try
        {
            string actor = currentActorSid();
            authorization.Demand(actor, AuthorizationOperation.PreviewProject);
            if (uri?.StartsWith(PosterPrefix, StringComparison.Ordinal) == true)
            {
                return CreatePosterResponse(uri, Posters, Drafts is null ? null : id => Drafts.GetDraft(id) is not null);
            }

            if (uri?.StartsWith(ProxyPrefix, StringComparison.Ordinal) == true)
            {
                return CreateProxyResponse(uri, rangeHeader, Proxies, Drafts is null ? null : id => Drafts.GetDraft(id) is not null);
            }

            if (!SourceOnlyMediaAssetReference.TryParse(uri, out SourceOnlyMediaAssetReference? reference))
            {
                return Empty(404, "Not Found");
            }

            StoredSourceOnlyExecution? execution = executionStore.Get(reference.ExecutionId);
            if (execution is null || execution.Request.ProjectId != reference.ProjectId || execution.ResultJson is null)
            {
                return Empty(404, "Not Found");
            }

            SourceOnlyResultLoadResult loaded = await resultService.LoadAsync(execution, cancellationToken).ConfigureAwait(false);
            if (!loaded.Validation.IsValid)
            {
                return Empty(404, "Not Found");
            }

            SourceOnlyResultArtifact? output = loaded.Result.Artifacts.SingleOrDefault(item => item.ArtifactId == reference.ArtifactId);
            if (output is null || output.ArtifactDigest != reference.ContentDigest ||
                output.Artifact.Kind != ArtifactKind.Render || output.MediaType is not ("video/mp4" or "image/png"))
            {
                return Empty(404, "Not Found");
            }

            // Reopen and retain the validated handle, rather than reopening a path after
            // checking it. ArtifactStore verifies path safety, hash, length and stable DB
            // registration; PlayerMediaContentStream owns the handle after this returns.
            held = await artifactStore.OpenValidatedReadAsync(output.ArtifactId, cancellationToken).ConfigureAwait(false);
            if (held.Id != reference.ArtifactId || held.ProjectId != reference.ProjectId || held.Kind != ArtifactKind.Render ||
                held.ContentDigest != reference.ContentDigest || held.ByteSize != output.ByteSize || held.Length != output.ByteSize)
            {
                return Empty(404, "Not Found");
            }

            StoredSourceOnlyExecution? current = executionStore.Get(reference.ExecutionId);
            if (current is null || current.Request.ProjectId != reference.ProjectId ||
                current.RequestDigest != execution.RequestDigest || current.ResultDigest != execution.ResultDigest ||
                current.ResultJson != execution.ResultJson || current.VersionId != execution.VersionId || current.VersionDigest != execution.VersionDigest ||
                !string.Equals(actor, currentActorSid(), StringComparison.Ordinal))
            {
                return Empty(404, "Not Found");
            }

            authorization.Demand(actor, AuthorizationOperation.PreviewProject);
            cancellationToken.ThrowIfCancellationRequested();
            if (rangeHeader?.Length > MaximumRangeHeaderCharacters ||
                !PlayerMediaByteRange.TryResolve(rangeHeader, held.ByteSize, out PlayerMediaByteRange range))
            {
                return new PlayerMediaResponse(416, "Range Not Satisfiable",
                    Headers(null, 0, string.Create(CultureInfo.InvariantCulture, $"bytes */{held.ByteSize}"), acceptRanges: true), null);
            }

            string headers = Headers(output.MediaType, range.Length, range.IsPartial
                ? string.Create(CultureInfo.InvariantCulture, $"bytes {range.Start}-{range.End}/{range.TotalLength}") : null,
                acceptRanges: true);
            var content = new PlayerMediaContentStream(held, range.Start, range.Length);
            held = null;
            return new PlayerMediaResponse(range.IsPartial ? 206 : 200, range.IsPartial ? "Partial Content" : "OK", headers, content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Indistinguishable denial/missing/invalid: never include exception messages,
            // storage references, local file names or private evidence in HTTP headers.
            return Empty(404, "Not Found");
        }
        finally
        {
            if (held is not null) { await held.DisposeAsync().ConfigureAwait(false); }
        }
    }

    /// <summary>FR-053: GET of a poster; the caller has already authorized PreviewProject.</summary>
    internal static PlayerMediaResponse CreatePosterResponse(string uri, ProjectPosterStore? posters,
        Func<EntityId<ProjectCreationDraftIdTag>, bool>? isListed)
    {
        // Same strictness as generated media: exact canonical IDs and lowercase hex only.
        string[] parts = uri.StartsWith(PosterPrefix, StringComparison.Ordinal) ? uri[PosterPrefix.Length..].Split('/') : [];
        if (posters is null || isListed is null || parts.Length != 2 ||
            !EntityId<ProjectCreationDraftIdTag>.TryParse(parts[0], out EntityId<ProjectCreationDraftIdTag>? draftId) ||
            draftId.ToString() != parts[0] ||
            !ContentDigest.TryParse(parts[1], out ContentDigest? digest) || digest.Hex != parts[1] ||
            !string.Equals(uri, PosterUri(draftId, digest), StringComparison.Ordinal) ||
            !isListed(draftId))
        {
            return Empty(404, "Not Found");
        }

        byte[]? bytes = posters.ReadValidated(draftId, digest);
        return bytes is null
            ? Empty(404, "Not Found")
            : new PlayerMediaResponse(200, "OK", Headers("image/jpeg", bytes.LongLength, null, acceptRanges: false),
                new MemoryStream(bytes, writable: false));
    }

    /// <summary>FR-144: ranged GET of a Project's proxy video; the caller has already authorized PreviewProject.</summary>
    internal static PlayerMediaResponse CreateProxyResponse(string uri, string? rangeHeader, ProjectProxyService? proxies,
        Func<EntityId<ProjectCreationDraftIdTag>, bool>? isListed)
    {
        string[] parts = uri.StartsWith(ProxyPrefix, StringComparison.Ordinal) ? uri[ProxyPrefix.Length..].Split('/') : [];
        if (proxies is null || isListed is null || parts.Length != 2 ||
            !EntityId<ProjectCreationDraftIdTag>.TryParse(parts[0], out EntityId<ProjectCreationDraftIdTag>? draftId) ||
            draftId.ToString() != parts[0] ||
            parts[1].Length != 32 || !parts[1].All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')) ||
            !isListed(draftId))
        {
            return Empty(404, "Not Found");
        }

        ProxyReadHandle? handle = proxies.OpenRead(draftId, parts[1]);
        if (handle is null)
        {
            return Empty(404, "Not Found");
        }

        try
        {
            if (rangeHeader?.Length > MaximumRangeHeaderCharacters ||
                handle.Length <= 0 ||
                !PlayerMediaByteRange.TryResolve(rangeHeader, handle.Length, out PlayerMediaByteRange range))
            {
                return new PlayerMediaResponse(416, "Range Not Satisfiable",
                    Headers(null, 0, string.Create(CultureInfo.InvariantCulture, $"bytes */{handle.Length}"), acceptRanges: true), null);
            }

            string headers = Headers("video/mp4", range.Length, range.IsPartial
                ? string.Create(CultureInfo.InvariantCulture, $"bytes {range.Start}-{range.End}/{range.TotalLength}") : null,
                acceptRanges: true);
            var content = new ProxyRangeStream(handle, range.Start, range.Length);
            handle = null;
            return new PlayerMediaResponse(range.IsPartial ? 206 : 200, range.IsPartial ? "Partial Content" : "OK", headers, content);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal static PlayerMediaResponse Unavailable() => Empty(503, "Service Unavailable");
    private static PlayerMediaResponse Empty(int status, string reason, bool allowGet = false) =>
        new(status, reason, Headers(null, 0, null, acceptRanges: false, allowGet), null);

    private static string Headers(string? contentType, long length, string? contentRange, bool acceptRanges, bool allowGet = false)
    {
        var headers = new List<string>();
        if (contentType is not null) { headers.Add("Content-Type: " + contentType); }
        headers.Add(string.Create(CultureInfo.InvariantCulture, $"Content-Length: {length}"));
        if (acceptRanges) { headers.Add("Accept-Ranges: bytes"); }
        if (contentRange is not null) { headers.Add("Content-Range: " + contentRange); }
        if (allowGet) { headers.Add("Allow: GET"); }
        headers.Add("Cache-Control: no-store");
        headers.Add("X-Content-Type-Options: nosniff");
        headers.Add("Cross-Origin-Resource-Policy: same-site");
        return string.Join("\r\n", headers);
    }
}
