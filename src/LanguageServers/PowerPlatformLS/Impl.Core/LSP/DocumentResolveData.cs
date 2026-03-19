// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.


namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    /// <summary>
    /// Base data type for all document based resolve handlers that stores the <see cref="TextDocumentIdentifier"/> for the resolve request.
    /// </summary>
    /// <param name="TextDocument">the text document associated with the request to resolve.</param>
    /// <remarks>Copied from https://github.com/dotnet/roslyn/blob/main/src/LanguageServer/Protocol/Handler/DocumentResolveData.cs</remarks>
    internal record DocumentResolveData(TextDocumentIdentifier TextDocument);
}