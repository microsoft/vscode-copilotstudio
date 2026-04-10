// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Impl.PullAgent/File/IFileAccessorFactory.cs

namespace Microsoft.CopilotStudio.Sync;

internal interface IFileAccessorFactory
{
    IFileAccessor Create(DirectoryPath root);
}
