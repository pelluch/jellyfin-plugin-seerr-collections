using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.SeerrPlaylists.Configuration;

public static class CoverImage
{
    private const string Name = "cover";

    private static string Folder => Plugin.Instance!.DataFolderPath;

    public static FileInfo? Find()
        => Directory.Exists(Folder)
            ? new DirectoryInfo(Folder).EnumerateFiles(Name + ".*").FirstOrDefault()
            : null;

    public static void Save(byte[] image, string extension)
    {
        Directory.CreateDirectory(Folder);
        var upload = Path.Combine(Folder, "upload.tmp");
        File.WriteAllBytes(upload, image);
        Delete();
        File.Move(upload, Path.Combine(Folder, Name + extension));
    }

    public static void Delete() => Find()?.Delete();
}
