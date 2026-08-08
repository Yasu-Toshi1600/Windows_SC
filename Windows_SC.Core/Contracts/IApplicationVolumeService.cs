using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Windows_SC.Models;

namespace Windows_SC.Services;


internal sealed record ApplicationAudioTarget(
    ApplicationAudioIdentifierKind IdentifierKind,
    string Identifier,
    string DisplayName);

internal sealed record ApplicationAudioInfo(
    ApplicationAudioTarget Target,
    bool IsAvailable,
    double VolumePercent,
    bool IsMixed,
    int SessionCount);

internal sealed record ApplicationVolumeResult(
    bool IsSuccess,
    string ErrorMessage,
    int SucceededSessionCount,
    int FailedSessionCount)
{
    public static ApplicationVolumeResult Success(int count) =>
        new(true, string.Empty, count, 0);

    public static ApplicationVolumeResult Failure(string message, int succeeded, int failed) =>
        new(false, message, succeeded, failed);
}

internal interface IApplicationVolumeService : IDisposable
{
    event EventHandler? StateChanged;

    IReadOnlyList<ApplicationAudioInfo> GetCachedApplications();

    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task<ApplicationVolumeResult> SetVolumeAsync(
        ApplicationAudioTarget target,
        int volumePercent,
        CancellationToken cancellationToken = default);
}

internal interface IApplicationAudioIdentityResolver
{
    ApplicationAudioTarget? Resolve(uint processId);
}
