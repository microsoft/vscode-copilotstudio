// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

internal sealed class KnowledgeFileTooLargeException : Exception
{
    public KnowledgeFileTooLargeException(string fileName, long maxBytes)
        : base($"Knowledge file '{fileName}' exceeded file size limit of {maxBytes} bytes and will be skipped.")
    {
        this.FileName = fileName;
        this.MaxBytes = maxBytes;
    }

    public string FileName { get; }

    public long MaxBytes { get; }
}
