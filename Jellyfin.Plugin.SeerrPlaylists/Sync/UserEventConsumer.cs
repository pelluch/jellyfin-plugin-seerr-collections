using System;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrPlaylists.Sync;

public class UserEventConsumer :
    IEventConsumer<UserCreatedEventArgs>,
    IEventConsumer<UserUpdatedEventArgs>,
    IEventConsumer<UserDeletedEventArgs>
{
    // Clients set up a new user's policy from the copy they got back before the tags were added, which drops them
    // again, and 12.0 raises no event for policy changes.
    private static readonly TimeSpan _recheckDelay = TimeSpan.FromSeconds(10);

    private readonly CollectionReconciler _reconciler;
    private readonly ILogger<UserEventConsumer> _logger;

    public UserEventConsumer(CollectionReconciler reconciler, ILogger<UserEventConsumer> logger)
    {
        _reconciler = reconciler;
        _logger = logger;
    }

    private static bool DryRun => Plugin.Instance!.Configuration.DryRun;

    public async Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        var userId = eventArgs.Argument.Id;
        await _reconciler.ProtectNewUserAsync(userId, DryRun).ConfigureAwait(false);
        _ = RecheckLaterAsync(userId);
    }

    public Task OnEvent(UserUpdatedEventArgs eventArgs) => _reconciler.RestoreBlockedTagsAsync(eventArgs.Argument.Id, DryRun);

    public Task OnEvent(UserDeletedEventArgs eventArgs) => _reconciler.RemoveOwnerAsync(eventArgs.Argument.Id, DryRun);

    private async Task RecheckLaterAsync(Guid userId)
    {
        try
        {
            await Task.Delay(_recheckDelay).ConfigureAwait(false);
            await _reconciler.RestoreBlockedTagsAsync(userId, DryRun).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Re-checking the blocked tags of new user {UserId} failed", userId);
        }
    }
}
