using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows_SC.Models;

namespace Windows_SC.Services;

internal sealed class ActionExecutionService(DiagnosticLogger logger) : IActionExecutionService
{
    public Task<ActionExecutionResult> ExecuteAsync(
        LauncherActionDefinition action,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            ProcessStartInfo startInfo = CreateStartInfo(action);
            Process? process = Process.Start(startInfo);
            string processId = process is null ? "unavailable" : process.Id.ToString();
            process?.Dispose();
            logger.Write(
                $"[Action] result=success kind={action.Kind} pid={processId} tracked=false");
            logger.WriteDetailed(
                $"[ActionDetail] result=success kind={action.Kind} " +
                $"target=\"{Sanitize(action.Target)}\"");
            return Task.FromResult(ActionExecutionResult.Success);
        }
        catch (Exception exception) when (exception is Win32Exception
            or InvalidOperationException
            or ArgumentException
            or IOException
            or NotSupportedException)
        {
            logger.Write(
                $"[Action] result=failed kind={action.Kind} " +
                $"exception={exception.GetType().Name}");
            logger.WriteDetailed(
                $"[ActionDetail] result=failed kind={action.Kind} " +
                $"target=\"{Sanitize(action.Target)}\" " +
                $"message=\"{Sanitize(exception.Message)}\"");
            return Task.FromResult(ActionExecutionResult.Failure(
                $"「{action.Target}」を起動できませんでした。\n{exception.Message}"));
        }
    }

    private static ProcessStartInfo CreateStartInfo(LauncherActionDefinition action)
    {
        string target = Environment.ExpandEnvironmentVariables(action.Target.Trim());
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("起動対象が設定されていません。");
        }

        string workingDirectory = Environment.ExpandEnvironmentVariables(
            action.WorkingDirectory.Trim());

        return action.Kind switch
        {
            LauncherActionKind.Command => CreateCommandStartInfo(
                BuildCommandLine(target, action.Arguments),
                workingDirectory,
                action.HideCommandWindow),
            LauncherActionKind.BatchFile => CreateCommandStartInfo(
                BuildCommandScriptLine(target, action.Arguments),
                string.IsNullOrWhiteSpace(workingDirectory)
                    ? Path.GetDirectoryName(target) ?? string.Empty
                    : workingDirectory,
                action.HideCommandWindow),
            _ when IsCommandScript(target) => CreateCommandStartInfo(
                BuildCommandScriptLine(target, action.Arguments),
                string.IsNullOrWhiteSpace(workingDirectory)
                    ? Path.GetDirectoryName(target) ?? string.Empty
                    : workingDirectory,
                action.HideCommandWindow),
            _ => CreateShellStartInfo(target, action.Arguments, workingDirectory)
        };
    }

    private static ProcessStartInfo CreateShellStartInfo(
        string target,
        string arguments,
        string workingDirectory) => new()
    {
        FileName = target,
        Arguments = arguments,
        WorkingDirectory = workingDirectory,
        UseShellExecute = true
    };

    private static ProcessStartInfo CreateCommandStartInfo(
        string commandLine,
        string workingDirectory,
        bool hideWindow)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = hideWindow,
            WindowStyle = hideWindow ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(commandLine);
        return startInfo;
    }

    private static string BuildCommandLine(string command, string arguments) =>
        string.IsNullOrWhiteSpace(arguments) ? command : $"{command} {arguments}";

    private static string BuildCommandScriptLine(string path, string arguments)
    {
        string escapedPath = path.Replace("\"", "\"\"");
        return string.IsNullOrWhiteSpace(arguments)
            ? $"call \"{escapedPath}\""
            : $"call \"{escapedPath}\" {arguments}";
    }

    private static bool IsCommandScript(string target) =>
        target.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
        || target.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);

    private static string Sanitize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');
}
