using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SeerrPlaylists.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrPlaylists.Sync;

public sealed class CollectionReconciler : IDisposable
{
    // Jellyfin's first metadata refresh of a new collection resets its display order, shortly after creation.
    private static readonly TimeSpan _displayOrderRecheckDelay = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly AsyncLocal<bool> _updatingPolicy = new();
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IUserManager _userManager;
    private readonly IProviderManager _providerManager;
    private readonly ILogger<CollectionReconciler> _logger;

    private volatile bool _uninstalling;

    public CollectionReconciler(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        IUserManager userManager,
        IProviderManager providerManager,
        ILogger<CollectionReconciler> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _userManager = userManager;
        _providerManager = providerManager;
        _logger = logger;
    }

    // UserUpdated is raised inside a user policy update, which this plugin also
    // affects. This is needed to tell when our own policy update is happening.
    public bool IsUpdatingPolicy => _updatingPolicy.Value;

    public async Task ReconcileAsync(
        IReadOnlyList<UserRequests> requests,
        string collectionName,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_uninstalling)
            {
                _logger.LogInformation("Skipping sync, the plugin is being uninstalled");
                return;
            }

            await ReconcileLockedAsync(requests, collectionName, dryRun, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ProtectNewUserAsync(Guid userId, bool dryRun)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await AddMissingBlockedTagsAsync(userId, dryRun).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    // On 12.1+ this runs inside the policy updates ReconcileAsync makes while holding the lock
    public Task RestoreBlockedTagsAsync(Guid userId, bool dryRun) => AddMissingBlockedTagsAsync(userId, dryRun);

    public async Task RemoveOwnerAsync(Guid userId, bool dryRun)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_uninstalling)
            {
                return;
            }

            var owned = FindOwnedCollections();
            foreach (var collection in owned[userId])
            {
                DeleteCollection(collection, dryRun);
            }

            await SyncBlockedTagsAsync(owned.Select(g => g.Key).Where(owner => owner != userId), dryRun).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(int Collections, int Users)> RemoveAllAsync(bool dryRun)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_uninstalling)
            {
                return (0, 0);
            }

            _logger.LogInformation("{DryRun}Removing every request collection and owner tag", DryRunPrefix(dryRun));
            return await RemoveAllLockedAsync(dryRun).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveEverythingAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _uninstalling = true;
            _logger.LogInformation("Uninstalling, so deleting every request collection and owner tag");
            try
            {
                await RemoveAllLockedAsync(dryRun: false).ConfigureAwait(false);
            }
            catch
            {
                _uninstalling = false;
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    // We lock to avoid a cover image changing mid-sync
    public async Task SaveCoverImageAsync(byte[] image, string extension)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            CoverImage.Save(image, extension);
            _logger.LogInformation("Saved a new {Extension} cover image of {Length} bytes", extension, image.Length);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteCoverImageAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            CoverImage.Delete();
            _logger.LogInformation("Removed the cover image");
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    private static string DryRunPrefix(bool dryRun) => dryRun ? "Dry run: " : string.Empty;

    private async Task ReconcileLockedAsync(
        IReadOnlyList<UserRequests> requests,
        string collectionName,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var desired = requests.Where(r => r.Items.Count > 0).ToDictionary(r => r.User.Id);
        var cover = CoverImage.Find();
        var stopwatch = Stopwatch.StartNew();
        var owned = FindOwnedCollections();
        if (Plugin.VerboseLogging)
        {
            _logger.LogInformation(
                "Found {Count} request collections in {ElapsedMs} ms",
                owned.Sum(g => g.Count()),
                stopwatch.ElapsedMilliseconds);
        }

        // Blocking every tag before touching collections means no collection is ever visible to another user.
        if (!dryRun)
        {
            await SyncBlockedTagsAsync(desired.Keys.Union(owned.Select(g => g.Key)), dryRun).ConfigureAwait(false);
        }

        foreach (var (userId, userRequests) in desired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stopwatch.Restart();
            var collection = owned[userId].FirstOrDefault();
            if (collection is null)
            {
                collection = await CreateCollectionAsync(userRequests, collectionName, dryRun, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await UpdateCollectionAsync(collection, userRequests, collectionName, dryRun, cancellationToken).ConfigureAwait(false);
            }

            if (collection is not null)
            {
                await SyncCoverImageAsync(collection, userId, cover, dryRun, cancellationToken).ConfigureAwait(false);
                WarnIfHiddenFromOwner(collection, userRequests.User);
            }

            if (Plugin.VerboseLogging)
            {
                _logger.LogInformation(
                    "Checked the collection of user {UserId} ({Count} items) in {ElapsedMs} ms",
                    userId.ToString("N"),
                    userRequests.Items.Count,
                    stopwatch.ElapsedMilliseconds);
            }
        }

        foreach (var group in owned)
        {
            foreach (var collection in group.Skip(desired.ContainsKey(group.Key) ? 1 : 0))
            {
                DeleteCollection(collection, dryRun);
            }
        }

        await SyncBlockedTagsAsync(desired.Keys, dryRun).ConfigureAwait(false);
    }

    private static bool HasOwnership(BoxSet collection, Guid userId, string collectionName)
        => collection.Name == collectionName
            && collection.DisplayOrder == nameof(ItemSortBy.Default)
            && collection.Tags.Where(OwnerTag.IsOwnerTag).SequenceEqual([OwnerTag.For(userId)], StringComparer.OrdinalIgnoreCase)
            && collection.LockedFields.Contains(MetadataField.Name)
            && collection.LockedFields.Contains(MetadataField.Tags);

    private static string Names(IEnumerable<BaseItem> items)
        => string.Join(", ", items.Select(i => i.Name).Order(StringComparer.CurrentCulture));

    private ILookup<Guid, BoxSet> FindOwnedCollections()
        => _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.BoxSet],
                Recursive = true,
            })
            .OfType<BoxSet>()
            .Select(collection => (Collection: collection, Owner: OwnerTag.FindOwner(collection.Tags)))
            .Where(c => c.Owner.HasValue)
            .OrderBy(c => c.Collection.DateCreated)
            .ToLookup(c => c.Owner!.Value, c => c.Collection);

    private async Task<(int Collections, int Users)> RemoveAllLockedAsync(bool dryRun)
    {
        var collections = FindOwnedCollections().SelectMany(g => g).ToList();
        foreach (var collection in collections)
        {
            DeleteCollection(collection, dryRun);
        }

        var users = await SyncBlockedTagsAsync([], dryRun).ConfigureAwait(false);
        return (collections.Count, users);
    }

    private void DeleteCollection(BoxSet collection, bool dryRun)
    {
        _logger.LogInformation("{DryRun}Deleting collection {Name} ({Id})", DryRunPrefix(dryRun), collection.Name, collection.Id);
        if (!dryRun)
        {
            _libraryManager.DeleteItem(collection, new DeleteOptions { DeleteFileLocation = true });
        }
    }

    private async Task AddMissingBlockedTagsAsync(Guid userId, bool dryRun)
    {
        var user = _userManager.GetUserById(userId);
        if (_uninstalling || user is null)
        {
            return;
        }

        var policy = _userManager.GetUserDto(user).Policy;
        var missing = FindOwnedCollections()
            .Select(g => g.Key)
            .Where(owner => owner != userId)
            .Select(OwnerTag.For)
            .Except(policy.BlockedTags, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "{DryRun}Blocking {Count} missing owner tags for user {UserId}",
            DryRunPrefix(dryRun),
            missing.Count,
            user.Id.ToString("N"));
        if (!dryRun)
        {
            policy.BlockedTags = [.. policy.BlockedTags, .. missing];
            await UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
        }
    }

    private async Task<int> SyncBlockedTagsAsync(IEnumerable<Guid> ownerIds, bool dryRun)
    {
        var stopwatch = Stopwatch.StartNew();
        var owners = ownerIds.ToHashSet();
        var users = _userManager.GetUsers().ToList();
        var updated = 0;
        foreach (var user in users)
        {
            var policy = _userManager.GetUserDto(user).Policy;
            var wanted = owners.Where(owner => owner != user.Id).Select(OwnerTag.For).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var current = policy.BlockedTags.Where(OwnerTag.IsOwnerTag).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (current.SetEquals(wanted))
            {
                continue;
            }

            updated++;
            _logger.LogInformation(
                "{DryRun}Updating blocked tags of user {UserId}, adding {Added} and removing {Removed}",
                DryRunPrefix(dryRun),
                user.Id.ToString("N"),
                wanted.Except(current, StringComparer.OrdinalIgnoreCase).Count(),
                current.Except(wanted, StringComparer.OrdinalIgnoreCase).Count());
            if (dryRun)
            {
                continue;
            }

            policy.BlockedTags = policy.BlockedTags.Where(t => !OwnerTag.IsOwnerTag(t)).Concat(wanted.Order(StringComparer.Ordinal)).ToArray();
            await UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
        }

        if (Plugin.VerboseLogging)
        {
            _logger.LogInformation(
                "Checked blocked tags of {UserCount} users in {ElapsedMs} ms, {UpdatedCount} needed changes",
                users.Count,
                stopwatch.ElapsedMilliseconds,
                updated);
        }

        return updated;
    }

    private async Task UpdatePolicyAsync(Guid userId, UserPolicy policy)
    {
        _updatingPolicy.Value = true;
        try
        {
            await _userManager.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);
        }
        finally
        {
            _updatingPolicy.Value = false;
        }
    }

    private async Task<BoxSet?> CreateCollectionAsync(
        UserRequests userRequests,
        string collectionName,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var (user, items) = userRequests;
        _logger.LogInformation(
            "{DryRun}Creating the collection of user {UserId} with {Count} items",
            DryRunPrefix(dryRun),
            user.Id.ToString("N"),
            items.Count);
        if (dryRun)
        {
            return null;
        }

        // The collection's folder is named after it, so a unique name keeps identically named collections apart.
        var collection = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
        {
            Name = "Seerr Requests " + user.Id.ToString("N"),
        }).ConfigureAwait(false);
        await TakeOwnershipAsync(collection, user.Id, collectionName, cancellationToken).ConfigureAwait(false);

        // Items go in only after the owner tag hides the collection from everyone else.
        await _collectionManager.AddToCollectionAsync(collection.Id, items.Select(i => i.Id)).ConfigureAwait(false);
        _ = RestoreDisplayOrderLaterAsync(collection.Id);
        return collection;
    }

    private async Task RestoreDisplayOrderLaterAsync(Guid collectionId)
    {
        try
        {
            await Task.Delay(_displayOrderRecheckDelay).ConfigureAwait(false);
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_uninstalling
                    || _libraryManager.GetItemById(collectionId) is not BoxSet collection
                    || collection.DisplayOrder == nameof(ItemSortBy.Default))
                {
                    return;
                }

                collection.DisplayOrder = nameof(ItemSortBy.Default);
                await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("Restored the display order of new collection {Name} ({Id})", collection.Name, collection.Id);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Restoring the display order of new collection {Id} failed", collectionId);
        }
    }

    private async Task UpdateCollectionAsync(
        BoxSet collection,
        UserRequests userRequests,
        string collectionName,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var (user, items) = userRequests;
        if (!HasOwnership(collection, user.Id, collectionName))
        {
            _logger.LogInformation(
                "{DryRun}Updating name, tag, display order and locks of collection {Name} ({Id}) of user {UserId}",
                DryRunPrefix(dryRun),
                collection.Name,
                collection.Id,
                user.Id.ToString("N"));
            if (!dryRun)
            {
                await TakeOwnershipAsync(collection, user.Id, collectionName, cancellationToken).ConfigureAwait(false);
            }
        }

        var current = collection.LinkedChildren.Where(c => c.ItemId.HasValue).Select(c => c.ItemId!.Value).ToHashSet();
        var toAdd = items.Where(i => !current.Contains(i.Id)).ToList();
        var toRemove = current.Except(items.Select(i => i.Id)).ToList();

        if (toAdd.Count > 0)
        {
            if (Plugin.VerboseLogging)
            {
                _logger.LogInformation(
                    "{DryRun}Adding {Count} items to the collection of user {UserId}: {Items}",
                    DryRunPrefix(dryRun),
                    toAdd.Count,
                    user.Id.ToString("N"),
                    Names(toAdd));
            }
            else
            {
                _logger.LogInformation(
                    "{DryRun}Adding {Count} items to the collection of user {UserId}",
                    DryRunPrefix(dryRun),
                    toAdd.Count,
                    user.Id.ToString("N"));
            }

            if (!dryRun)
            {
                await _collectionManager.AddToCollectionAsync(collection.Id, toAdd.Select(i => i.Id)).ConfigureAwait(false);
            }
        }

        if (toRemove.Count > 0)
        {
            if (Plugin.VerboseLogging)
            {
                _logger.LogInformation(
                    "{DryRun}Removing {Count} items from the collection of user {UserId}: {Items}",
                    DryRunPrefix(dryRun),
                    toRemove.Count,
                    user.Id.ToString("N"),
                    string.Join(", ", toRemove.Select(id => _libraryManager.GetItemById(id)?.Name ?? id.ToString("N"))));
            }
            else
            {
                _logger.LogInformation(
                    "{DryRun}Removing {Count} items from the collection of user {UserId}",
                    DryRunPrefix(dryRun),
                    toRemove.Count,
                    user.Id.ToString("N"));
            }

            if (!dryRun)
            {
                await _collectionManager.RemoveFromCollectionAsync(collection.Id, toRemove).ConfigureAwait(false);
            }
        }
    }

    private async Task TakeOwnershipAsync(BoxSet collection, Guid userId, string collectionName, CancellationToken cancellationToken)
    {
        collection.Name = collectionName;
        collection.DisplayOrder = nameof(ItemSortBy.Default);
        collection.Tags = collection.Tags.Where(t => !OwnerTag.IsOwnerTag(t)).Append(OwnerTag.For(userId)).ToArray();
        collection.LockedFields = collection.LockedFields.Union([MetadataField.Name, MetadataField.Tags]).ToArray();
        await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
    }

    private async Task SyncCoverImageAsync(BoxSet collection, Guid userId, FileInfo? cover, bool dryRun, CancellationToken cancellationToken)
    {
        var image = collection.GetImageInfo(ImageType.Primary, 0);
        if (cover is null)
        {
            if (image is not null)
            {
                _logger.LogInformation(
                    "{DryRun}Removing the cover image of collection {Name} ({Id}) of user {UserId}",
                    DryRunPrefix(dryRun),
                    collection.Name,
                    collection.Id,
                    userId.ToString("N"));
                if (!dryRun)
                {
                    await collection.DeleteImageAsync(ImageType.Primary, 0).ConfigureAwait(false);
                }
            }

            return;
        }

        // Jellyfin dates an image by its file, so an image saved after the cover is a copy of it.
        if (image is not null && image.DateModified >= cover.LastWriteTimeUtc)
        {
            return;
        }

        _logger.LogInformation(
            "{DryRun}Setting the cover image of collection {Name} ({Id}) of user {UserId}",
            DryRunPrefix(dryRun),
            collection.Name,
            collection.Id,
            userId.ToString("N"));
        if (dryRun)
        {
            return;
        }

        var stream = AsyncFile.OpenRead(cover.FullName);
        await using (stream.ConfigureAwait(false))
        {
            await _providerManager.SaveImage(collection, stream, MimeTypes.GetMimeType(cover.FullName), ImageType.Primary, null, cancellationToken)
                .ConfigureAwait(false);
        }

        await collection.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
    }

    private void WarnIfHiddenFromOwner(BoxSet collection, User user)
    {
        if (!_libraryManager.GetCollectionFolders(collection).Any(folder => folder.IsVisible(user)))
        {
            _logger.LogWarning(
                "User {UserId} cannot see their collection {Name}, because they have no access to the library that holds collections",
                user.Id.ToString("N"),
                collection.Name);
        }

        if (!collection.IsParentalAllowed(user, false))
        {
            _logger.LogWarning(
                "User {UserId} cannot see their collection {Name} because of their parental controls. Any allowed tags they have must include {Tag}",
                user.Id.ToString("N"),
                collection.Name,
                OwnerTag.For(user.Id));
        }
    }
}
