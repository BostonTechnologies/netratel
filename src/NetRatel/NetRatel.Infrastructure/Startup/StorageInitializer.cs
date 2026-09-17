using Microsoft.Extensions.Options;
using NetRatel.Application.Common;

namespace NetRatel.Infrastructure.Startup;

public sealed class StorageInitializer
{
    private readonly StorageOptions _options;

    public StorageInitializer(IOptions<StorageOptions> options)
    {
        _options = options.Value;
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(_options.RootPath);
        Directory.CreateDirectory(GetKeysDirectory());
    }

    public string GetKeysDirectory()
    {
        return Path.Combine(_options.RootPath, "keys");
    }
}
