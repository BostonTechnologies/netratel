using System.ComponentModel.DataAnnotations;

namespace NetRatel.Web.Services.FileSystem;

public sealed class FileBrowserOptions
{
    public const string SectionName = "FileBrowser";

    [Range(1, 32 * 1024 * 1024)]
    public int MaxPreviewBytes { get; init; } = 8 * 1024 * 1024;
}
