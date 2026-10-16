using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrPlaylists.Sync;

public sealed class CollectionReconciler : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<CollectionReconciler> _logger;

    private volatile bool _uninstalling;

    public CollectionReconciler(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        IUserManager userManager,
        ILogger<CollectionReconciler> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _userManager = userManager;
        _logger = logger;
    }

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

    public async Task RemoveEverythingAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _uninstalling = true;
            _logger.LogInformation("Uninstalling, so deleting every request collection and owner tag");
            try
            {
                foreach (var collection in FindOwnedCollections().SelectMany(g => g))
                {
                    DeleteCollection(collection, dryRun: false);
                }

                await SyncBlockedTagsAsync([], dryRun: false).ConfigureAwait(false);
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

    public void Dispose() => _lock.Dispose();

    private static string DryRunPrefix(bool dryRun) => dryRun ? "Dry run: " : string.Empty;

    private async Task ReconcileLockedAsync(
        IReadOnlyList<UserRequests> requests,
        string collectionName,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var desired = requests.Where(r => r.Items.Count > 0).ToDictionary(r => r.User.Id);
        var owned = FindOwnedCollections();

        // Blocking every tag before touching collections means no collection is ever visible to another user.
        if (!dryRun)
        {
            await SyncBlockedTagsAsync(desired.Keys.Union(owned.Select(g => g.Key)), dryRun).ConfigureAwait(false);
        }

        foreach (var (userId, userRequests) in desired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var collection = owned[userId].FirstOrDefault();
            if (collection is null)
            {
                await CreateCollectionAsync(userRequests, collectionName, dryRun, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await UpdateCollectionAsync(collection, userRequests, collectionName, dryRun, cancellationToken).ConfigureAwait(false);
            }

            WarnIfHiddenByAllowedTags(userRequests.User);
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
            "{DryRun}Blocking missing tags [{Tags}] for {User}",
            DryRunPrefix(dryRun),
            string.Join(", ", missing),
            user.Username);
        if (!dryRun)
        {
            policy.BlockedTags = [.. policy.BlockedTags, .. missing];
            await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
        }
    }

    private async Task SyncBlockedTagsAsync(IEnumerable<Guid> ownerIds, bool dryRun)
    {
        var owners = ownerIds.ToHashSet();
        foreach (var user in _userManager.GetUsers().ToList())
        {
            var policy = _userManager.GetUserDto(user).Policy;
            var wanted = owners.Where(owner => owner != user.Id).Select(OwnerTag.For).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var current = policy.BlockedTags.Where(OwnerTag.IsOwnerTag).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (current.SetEquals(wanted))
            {
                continue;
            }

            _logger.LogInformation(
                "{DryRun}Updating {User}'s blocked tags, adding [{Added}] and removing [{Removed}]",
                DryRunPrefix(dryRun),
                user.Username,
                string.Join(", ", wanted.Except(current, StringComparer.OrdinalIgnoreCase)),
                string.Join(", ", current.Except(wanted, StringComparer.OrdinalIgnoreCase)));
            if (dryRun)
            {
                continue;
            }

            policy.BlockedTags = policy.BlockedTags.Where(t => !OwnerTag.IsOwnerTag(t)).Concat(wanted.Order(StringComparer.Ordinal)).ToArray();
            await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
        }
    }

    private async Task CreateCollectionAsync(
        UserRequests userRequests,
        string collectionName,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var (user, items) = userRequests;
        _logger.LogInformation(
            "{DryRun}Creating {User}'s collection with {Count} items: {Items}",
            DryRunPrefix(dryRun),
            user.Username,
            items.Count,
            Names(items));
        if (dryRun)
        {
            return;
        }

        // The collection's folder is named after it, so a unique name keeps identically named collections apart.
        var collection = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
        {
            Name = "Seerr Requests " + user.Id.ToString("N"),
        }).ConfigureAwait(false);
        await TakeOwnershipAsync(collection, user.Id, collectionName, cancellationToken).ConfigureAwait(false);

        // Items go in only after the owner tag hides the collection from everyone else.
        await _collectionManager.AddToCollectionAsync(collection.Id, items.Select(i => i.Id)).ConfigureAwait(false);
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
                "{DryRun}Updating name, tag and locks of {User}'s collection {Name} ({Id})",
                DryRunPrefix(dryRun),
                user.Username,
                collection.Name,
                collection.Id);
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
            _logger.LogInformation(
                "{DryRun}Adding {Count} items to {User}'s collection: {Items}",
                DryRunPrefix(dryRun),
                toAdd.Count,
                user.Username,
                Names(toAdd));
            if (!dryRun)
            {
                await _collectionManager.AddToCollectionAsync(collection.Id, toAdd.Select(i => i.Id)).ConfigureAwait(false);
            }
        }

        if (toRemove.Count > 0)
        {
            _logger.LogInformation(
                "{DryRun}Removing {Count} items from {User}'s collection: {Items}",
                DryRunPrefix(dryRun),
                toRemove.Count,
                user.Username,
                string.Join(", ", toRemove.Select(id => _libraryManager.GetItemById(id)?.Name ?? id.ToString("N"))));
            if (!dryRun)
            {
                await _collectionManager.RemoveFromCollectionAsync(collection.Id, toRemove).ConfigureAwait(false);
            }
        }
    }

    private async Task TakeOwnershipAsync(BoxSet collection, Guid userId, string collectionName, CancellationToken cancellationToken)
    {
        collection.Name = collectionName;
        collection.Tags = collection.Tags.Where(t => !OwnerTag.IsOwnerTag(t)).Append(OwnerTag.For(userId)).ToArray();
        collection.LockedFields = collection.LockedFields.Union([MetadataField.Name, MetadataField.Tags]).ToArray();
        await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
    }

    private void WarnIfHiddenByAllowedTags(User user)
    {
        var allowedTags = _userManager.GetUserDto(user).Policy.AllowedTags;
        if (allowedTags.Length > 0 && !allowedTags.Contains(OwnerTag.For(user.Id), StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "{User} has allowed tags set, so they cannot see their collection unless {Tag} is one of them",
                user.Username,
                OwnerTag.For(user.Id));
        }
    }
}
