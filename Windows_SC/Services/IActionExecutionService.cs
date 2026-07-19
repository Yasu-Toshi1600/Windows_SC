using System.Threading;
using System.Threading.Tasks;
using Windows_SC.Models;

namespace Windows_SC.Services;

internal interface IActionExecutionService
{
    Task<ActionExecutionResult> ExecuteAsync(
        LauncherActionDefinition action,
        CancellationToken cancellationToken = default);
}

internal readonly record struct ActionExecutionResult(
    bool IsSuccess,
    string ErrorMessage)
{
    public static ActionExecutionResult Success => new(true, string.Empty);

    public static ActionExecutionResult Failure(string message) => new(false, message);
}
