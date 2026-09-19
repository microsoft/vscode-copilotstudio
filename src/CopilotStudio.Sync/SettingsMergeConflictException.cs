// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

public sealed class SettingsMergeConflictException : Exception
{
    public SettingsMergeConflictException()
        : base(DefaultMessage)
    {
    }

    public SettingsMergeConflictException(string message)
        : base(message)
    {
    }

    public SettingsMergeConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal const string DefaultMessage = "Local and remote changes to the agent settings conflict. Review settings.mcs.yml and try again.";
}
