// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CommonLanguageServerProtocol.Framework
{
    using System.Collections.Immutable;

    /// <summary>
    /// Optional interface that can be implemented by <see cref="ILspServices"/> implementations
    /// to provide faster access to <see cref="IMethodHandler"/>s.
    /// </summary>
    internal interface IMethodHandlerProvider
    {
        ImmutableArray<(IMethodHandler? Instance, TypeRef HandlerTypeRef, ImmutableArray<MethodHandlerDetails> HandlerDetails)> GetMethodHandlers();
    }
}
