using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.SeerrPlaylists.Configuration;

public sealed class CoverImage
{
    private const string Name = "cover";

    private readonly string _folder;

    public CoverImage(IApplicationPaths applicationPaths)
    {
        _folder = Path.Combine(applicationPaths.DataPath, "seerr-collections");
    }

    public FileInfo? Find()
        => Directory.Exists(_folder)
            ? new DirectoryInfo(_folder).EnumerateFiles(Name + ".*").FirstOrDefault()
            : null;

    public void Save(byte[] image, string extension)
    {
        Directory.CreateDirectory(_folder);
        var upload = Path.Combine(_folder, "upload.tmp");
        File.WriteAllBytes(upload, image);
        Delete();
        File.Move(upload, Path.Combine(_folder, Name + extension));
    }

    public void Delete() => Find()?.Delete();
}
