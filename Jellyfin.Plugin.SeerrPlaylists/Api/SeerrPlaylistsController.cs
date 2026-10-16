using System;
using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SeerrPlaylists.Seerr;
using Jellyfin.Plugin.SeerrPlaylists.Sync;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SeerrPlaylists.Api;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("SeerrPlaylists")]
[Produces(MediaTypeNames.Application.Json)]
public class SeerrPlaylistsController : ControllerBase
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(20);

    private readonly SeerrConnectionVerifier _verifier;
    private readonly CollectionReconciler _reconciler;

    public SeerrPlaylistsController(SeerrConnectionVerifier verifier, CollectionReconciler reconciler)
    {
        _verifier = verifier;
        _reconciler = reconciler;
    }

    [HttpPost("TestConnection")]
    public async Task<ActionResult<ConnectionTestResult>> TestConnection(
        [FromBody, Required] ConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_testTimeout);
        try
        {
            var info = await _verifier.VerifyAsync(SeerrConnection.Create(request.Url, request.ApiKey), deadline.Token).ConfigureAwait(false);
            return new ConnectionTestResult(true, null, info.Version, info.JellyfinServerName);
        }
        catch (SeerrException e)
        {
            return new ConnectionTestResult(false, e.Message, null, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ConnectionTestResult(false, $"Seerr did not answer within {_testTimeout.TotalSeconds} seconds.", null, null);
        }
    }

    [HttpPost("RemoveCollections")]
    public async Task<ActionResult<RemovalResult>> RemoveCollections([FromBody, Required] RemovalRequest request)
    {
        var (collections, users) = await _reconciler.RemoveAllAsync(request.DryRun).ConfigureAwait(false);
        return new RemovalResult(request.DryRun, collections, users, Plugin.Instance!.Configuration.SyncEnabled);
    }
}
