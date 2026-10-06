using System.Collections.Concurrent;
using CleanBoost.Core;
using Microsoft.Win32;

namespace CleanBoost.Features.LargeOldFiles;

/// <summary>Loại file dùng cho bộ lọc File lớn &amp; cũ (FileKind của bản Mac), thứ tự giữ như <c>allCases</c>.</summary>
public enum FileKind { Documents, Images, Videos, Audio, Archives, Apps, Other }

/// <summary>
/// Phân loại theo đuôi file: bảng đuôi cố định (thay cho UTType của bản Mac), rồi <c>PerceivedType</c> trong
/// <c>HKCR\.ext</c> cho đuôi lạ.
/// </summary>
public static class FileKinds
{
    public static IReadOnlyList<FileKind> All { get; } = Enum.GetValues<FileKind>();

    public static string Title(this FileKind kind) => kind switch
    {
        FileKind.Documents => Loc.T("Tài liệu"),
        FileKind.Images => Loc.T("Ảnh"),
        FileKind.Videos => Loc.T("Video"),
        FileKind.Audio => Loc.T("Âm thanh"),
        FileKind.Archives => Loc.T("Lưu trữ & ổ đĩa ảo"),
        FileKind.Apps => Loc.T("Ứng dụng & bộ cài"),
        _ => Loc.T("Khác"),
    };

    /// <summary>Tên biểu tượng kiểu SF Symbol (giao diện đổi sang glyph Segoe).</summary>
    public static string Symbol(this FileKind kind) => kind switch
    {
        FileKind.Documents => "doc.text",
        FileKind.Images => "photo",
        FileKind.Videos => "film",
        FileKind.Audio => "music.note",
        FileKind.Archives => "archivebox",
        FileKind.Apps => "shippingbox",
        _ => "doc",
    };

    // Danh sách tường minh của bản Mac, thêm định dạng ổ ảo/bộ cài của Windows.
    private static readonly HashSet<string> s_archives = new(StringComparer.Ordinal)
    {
        "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "lz4", "dmg", "iso", "img", "sparseimage", "sparsebundle",
        "vmdk", "vdi", "qcow2", "vhd", "vhdx", "hdd", "ova", "ipsw", "xip", "cpgz",
        "zipx", "cab", "wim", "esd", "swm", "lzh", "lha", "arj", "z", "lzma", "tbz", "tbz2", "txz", "tlz", "jar", "war", "avhdx", "ovf", "vmcx", "vmrs",
    };

    private static readonly HashSet<string> s_apps = new(StringComparer.Ordinal)
    {
        "app", "pkg", "mpkg", "exe", "msi", "apk", "ipa", "deb", "rpm", "appimage",
        "msp", "msix", "msixbundle", "appx", "appxbundle", "appinstaller", "xapk", "dll", "so", "dylib",
    };

    private static readonly HashSet<string> s_videos = new(StringComparer.Ordinal)
    {
        "mp4", "m4v", "mov", "qt", "mkv", "avi", "wmv", "webm", "flv", "f4v", "mpg", "mpeg", "mpe", "m2v", "3gp", "3g2", "ts", "mts", "m2ts",
        "vob", "ogv", "divx", "asf", "rm", "rmvb", "dv", "mxf", "insv", "braw", "r3d",
    };

    private static readonly HashSet<string> s_audio = new(StringComparer.Ordinal)
    {
        "mp3", "wav", "flac", "aac", "m4a", "m4b", "m4p", "ogg", "oga", "opus", "wma", "aif", "aiff", "aifc", "alac", "ape", "mid", "midi",
        "amr", "caf", "dsf", "dff", "wv", "mka",
    };

    private static readonly HashSet<string> s_images = new(StringComparer.Ordinal)
    {
        "jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "dib", "tif", "tiff", "heic", "heif", "webp", "avif", "jxl", "ico", "icns", "svg",
        "psd", "psb", "raw", "cr2", "cr3", "crw", "nef", "nrw", "arw", "srf", "sr2", "dng", "raf", "orf", "rw2", "pef", "srw", "x3f", "exr",
        "hdr", "tga", "dds", "xcf", "kra",
    };

    private static readonly HashSet<string> s_documents = new(StringComparer.Ordinal)
    {
        "pdf", "doc", "docx", "docm", "dot", "dotx", "rtf", "odt", "ott", "txt", "md", "markdown", "csv", "tsv", "xls", "xlsx", "xlsm", "xlsb",
        "ods", "ppt", "pptx", "pptm", "pps", "ppsx", "odp", "key", "pages", "numbers", "epub", "mobi", "azw", "azw3", "djvu", "xps", "oxps",
        "one", "vsd", "vsdx", "pub", "tex", "log", "json", "xml", "yaml", "yml", "html", "htm", "sql", "ipynb",
    };

    private static readonly ConcurrentDictionary<string, FileKind> s_perceived = new(StringComparer.Ordinal);

    public static FileKind Classify(string path) => ClassifyExtension(Path.GetExtension(path));

    public static FileKind ClassifyExtension(string? extension)
    {
        var ext = (extension ?? "").TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0) return FileKind.Other;
        if (s_archives.Contains(ext)) return FileKind.Archives;
        if (s_apps.Contains(ext)) return FileKind.Apps;
        if (s_videos.Contains(ext)) return FileKind.Videos;
        if (s_audio.Contains(ext)) return FileKind.Audio;
        if (s_images.Contains(ext)) return FileKind.Images;
        if (s_documents.Contains(ext)) return FileKind.Documents;
        return s_perceived.GetOrAdd(ext, Perceived);
    }

    /// <summary>Loại Windows gán cho đuôi file (<c>PerceivedType</c>, rồi <c>Content Type</c>).</summary>
    private static FileKind Perceived(string ext)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey("." + ext);
            if (key is null) return FileKind.Other;
            var perceived = (key.GetValue("PerceivedType") as string)?.ToLowerInvariant();
            switch (perceived)
            {
                case "video": return FileKind.Videos;
                case "audio": return FileKind.Audio;
                case "image": return FileKind.Images;
                case "compressed": return FileKind.Archives;
                case "application": return FileKind.Apps;
                case "text" or "document": return FileKind.Documents;
            }
            var mime = (key.GetValue("Content Type") as string)?.ToLowerInvariant() ?? "";
            if (mime.StartsWith("video/", StringComparison.Ordinal)) return FileKind.Videos;
            if (mime.StartsWith("audio/", StringComparison.Ordinal)) return FileKind.Audio;
            if (mime.StartsWith("image/", StringComparison.Ordinal)) return FileKind.Images;
            if (mime.StartsWith("text/", StringComparison.Ordinal)) return FileKind.Documents;
        }
        catch
        {
            // khoá không đọc được: coi là "Khác"
        }
        return FileKind.Other;
    }
}
