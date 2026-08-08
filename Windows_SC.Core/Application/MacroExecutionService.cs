using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows_SC.Models;

namespace Windows_SC.Services;

internal sealed class MacroExecutionService : IMacroExecutionService
{
    private static readonly TimeSpan MaximumExecutionTime = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IActionExecutionService _actionExecutionService;
    private readonly Action<string> _writeLog;
    private bool _isDisposed;

    internal MacroExecutionService(
        IActionExecutionService actionExecutionService,
        Action<string> writeLog)
    {
        _actionExecutionService = actionExecutionService;
        _writeLog = writeLog;
    }

    public async Task<ActionExecutionResult> ExecuteAsync(
        Guid itemId,
        MacroDefinition macro,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!await _executionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return ActionExecutionResult.Failure("別のマクロを実行中です。");
        }

        using CancellationTokenSource timeout = new(MaximumExecutionTime);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token,
            timeout.Token);
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            _writeLog(
                $"[Macro] action=execute result=started itemId={itemId} " +
                $"steps={macro.Steps.Count}");
            for (int index = 0; index < macro.Steps.Count; index++)
            {
                MacroStepDefinition step = macro.Steps[index];
                linked.Token.ThrowIfCancellationRequested();
                _writeLog(
                    $"[Macro] action=step result=started itemId={itemId} " +
                    $"step={index + 1} kind={step.Kind}");

                if (step.Kind == MacroStepKind.Wait)
                {
                    await Task.Delay(step.DelayMilliseconds, linked.Token).ConfigureAwait(false);
                }
                else
                {
                    ActionExecutionResult result = await _actionExecutionService.ExecuteAsync(
                        step.Action ?? new LauncherActionDefinition(),
                        linked.Token).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        _writeLog(
                            $"[Macro] action=step result=failed itemId={itemId} " +
                            $"step={index + 1} kind={step.Kind}");
                        return ActionExecutionResult.Failure(
                            $"マクロのステップ{index + 1}「{step.DisplayName}」で停止しました。\n" +
                            result.ErrorMessage);
                    }
                }

                _writeLog(
                    $"[Macro] action=step result=success itemId={itemId} " +
                    $"step={index + 1} kind={step.Kind}");
            }

            _writeLog(
                $"[Macro] action=execute result=success itemId={itemId} " +
                $"steps={macro.Steps.Count} elapsed-ms={stopwatch.ElapsedMilliseconds}");
            return ActionExecutionResult.Success;
        }
        catch (OperationCanceledException)
        {
            string reason = timeout.IsCancellationRequested ? "timeout" : "cancelled";
            _writeLog(
                $"[Macro] action=execute result=failed itemId={itemId} reason={reason} " +
                $"elapsed-ms={stopwatch.ElapsedMilliseconds}");
            return ActionExecutionResult.Failure(
                reason == "timeout"
                    ? "マクロの実行時間が60秒を超えたため停止しました。"
                    : "マクロをキャンセルしました。");
        }
        finally
        {
            _executionGate.Release();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _shutdown.Cancel();
        _shutdown.Dispose();
        // 実行中のfinallyがReleaseする可能性があるため、ここでは破棄しない。
    }
}
