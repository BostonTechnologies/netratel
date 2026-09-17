using System.Threading;
using System.Threading.Tasks;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Scripts;

namespace NetRatel.Web.Services.Script;

public interface IScriptLibraryService
{
    Task<PagedResult<ScriptSummaryDto>> SearchAsync(
        string? search = null,
        int page = 1,
        int pageSize = 25,
        CancellationToken cancellationToken = default);

    Task<ScriptSummaryDto?> GetByIdAsync(ulong id, CancellationToken cancellationToken = default);
}
