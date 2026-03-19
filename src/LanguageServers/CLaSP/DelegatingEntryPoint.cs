// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CommonLanguageServerProtocol.Framework
{
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;

    public abstract class DelegatingEntryPoint<TRequestContext>
    {
        protected readonly string _method;

        public DelegatingEntryPoint(string method)
        {
            _method = method;
        }

        public abstract MethodInfo GetEntryPoint(bool hasParameter);

        protected async Task<object?> InvokeAsync(
            IRequestExecutionQueue<TRequestContext> queue,
            object? requestObject,
            ILspServices lspServices,
            CancellationToken cancellationToken)
        {
            var result = await queue.ExecuteAsync(requestObject, _method, lspServices, cancellationToken).ConfigureAwait(false);
            if (result == NoValue.Instance)
            {
                return null;
            }
            else
            {
                return result;
            }
        }
    }
}