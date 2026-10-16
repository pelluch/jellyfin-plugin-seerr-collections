using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SeerrPlaylists.Configuration;
using Jellyfin.Plugin.SeerrPlaylists.Seerr;
using Jellyfin.Plugin.SeerrPlaylists.Sync;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Net;
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
    private readonly SyncScheduler _scheduler;

    public SeerrPlaylistsController(SeerrConnectionVerifier verifier, CollectionReconciler reconciler, SyncScheduler scheduler)
    {
        _verifier = verifier;
        _reconciler = reconciler;
        _scheduler = scheduler;
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

    [HttpGet("CoverImage")]
    public ActionResult GetCoverImage()
    {
        var cover = CoverImage.Find();
        return cover is null ? NotFound() : PhysicalFile(cover.FullName, MimeTypes.GetMimeType(cover.FullName));
    }

    [HttpPost("CoverImage")]
    [Consumes(MediaTypeNames.Image.Jpeg, MediaTypeNames.Image.Png, MediaTypeNames.Image.Webp)]
    public async Task<ActionResult<CoverImageResult>> UploadCoverImage(CancellationToken cancellationToken)
    {
        byte[] image;
        using (var reader = new StreamReader(Request.Body))
        {
            try
            {
                // ApiClient requires a string, so we use base64
                image = Convert.FromBase64String(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (FormatException)
            {
                return BadRequest("The image is not base64 encoded.");
            }
        }

        await _reconciler.SaveCoverImageAsync(image, MimeTypes.ToExtension(Request.ContentType!)!).ConfigureAwait(false);
        return CoverImageChanged();
    }

    [HttpDelete("CoverImage")]
    public async Task<ActionResult<CoverImageResult>> DeleteCoverImage()
    {
        await _reconciler.DeleteCoverImageAsync().ConfigureAwait(false);
        return CoverImageChanged();
    }

    private CoverImageResult CoverImageChanged()
    {
        _scheduler.SyncSoon();
        var configuration = Plugin.Instance!.Configuration;
        return new CoverImageResult(configuration.SyncEnabled, configuration.DryRun);
    }
}
