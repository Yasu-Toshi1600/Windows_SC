using System.Threading;
using System.Threading.Tasks;
using Windows_SC.Models;

namespace Windows_SC.Services;

internal interface ISettingsRepository
{
    Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default);
}
