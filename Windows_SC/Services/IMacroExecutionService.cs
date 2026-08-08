using System;
using System.Threading;
using System.Threading.Tasks;
using Windows_SC.Models;

namespace Windows_SC.Services;

internal interface IMacroExecutionService : IDisposable
{
    Task<ActionExecutionResult> ExecuteAsync(
        Guid itemId,
        MacroDefinition macro,
        CancellationToken cancellationToken = default);
}
