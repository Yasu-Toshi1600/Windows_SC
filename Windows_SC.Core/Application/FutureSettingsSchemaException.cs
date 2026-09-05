using System;

namespace Windows_SC.Services;

internal sealed class FutureSettingsSchemaException : Exception
{
    public FutureSettingsSchemaException(long foundVersion, int supportedVersion)
        : base(
            $"Settings schema {foundVersion} is newer than the supported schema " +
            $"{supportedVersion}.")
    {
        FoundVersion = foundVersion;
        SupportedVersion = supportedVersion;
    }

    public long FoundVersion { get; }

    public int SupportedVersion { get; }
}
