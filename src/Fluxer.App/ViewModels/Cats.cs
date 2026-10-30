namespace Fluxer.App.ViewModels;

/// Name, icon and tint of a category key: the built-in ones are translated and have their own icon and colour,
/// the user's own share a tag icon and the "custom" colour, tags ("#work") a hash.
public static class Cats
{
    public static string Title(string? key) => key switch
    {
        "all" => Strings.CatAll,
        "Video" => Strings.CatVideo,
        "Music" => Strings.CatMusic,
        "Documents" => Strings.CatDocuments,
        "Archives" => Strings.CatArchives,
        "Programs" => Strings.CatPrograms,
        "Torrent" => Strings.CatTorrent,
        null or "" => Strings.CatNone,
        ['#', .. var tag] => tag,
        _ => key,
    };

    public static string IconKey(string? key) => key switch
    {
        "all" => "IcAll",
        "Video" => "IcVideo",
        "Music" => "IcMusic",
        "Documents" => "IcDoc",
        "Archives" => "IcArchive",
        "Programs" => "IcApp",
        "Torrent" => "IcTorrent",
        null or "" => "IcFile",
        ['#', ..] => "IcHash",
        _ => "IcTag",
    };

    public static string TintKey(string? key) => key switch
    {
        "Video" or "Music" or "Documents" or "Archives" or "Programs" or "Torrent" => $"Cat{key}Brush",
        null or "" => "CatOtherBrush",
        _ => "CatCustomBrush",
    };

    /// The user's own categories and tags show their name in the filter row; built-in ones only an icon.
    public static bool ShowsName(string key) => key != "all" && !Fluxer.Core.Downloads.Category.BuiltIn.Contains(key);
}
