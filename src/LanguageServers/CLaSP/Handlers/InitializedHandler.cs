// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CommonLanguageServerProtocol.Framework.Handlers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint("initialized", LanguageServerConstants.DefaultLanguageName)]
    public class InitializedHandler<TRequest, TRequestContext> : INotificationHandler<TRequest, TRequestContext>
    {
        private bool _hasBeenInitialized = false;

        public bool MutatesSolutionState => true;

        public Task HandleNotificationAsync(TRequest request, TRequestContext requestContext, CancellationToken cancellationToken)
        {
            if (_hasBeenInitialized)
            {
                throw new InvalidOperationException("initialized was called twice");
            }

            _hasBeenInitialized = true;

            return Task.CompletedTask;
        }
    }
}
