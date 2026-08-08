using System.Threading;
using System.Threading.Tasks;
using Windows_SC.Models;

namespace Windows_SC.Services;


internal interface IShortcutKeyExecutionService
{
    Task<ActionExecutionResult> ExecuteAsync(
        ShortcutKeyDefinition shortcutKey,
        CancellationToken cancellationToken = default);
}
