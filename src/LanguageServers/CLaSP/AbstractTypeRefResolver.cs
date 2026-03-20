// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CommonLanguageServerProtocol.Framework
{
    using System;
    using System.Collections.Concurrent;

    public abstract class AbstractTypeRefResolver
    {
        private readonly ConcurrentDictionary<TypeRef, Type?> _typeRefToTypeMap = [];

        public Type? Resolve(TypeRef typeRef)
        {
            if (_typeRefToTypeMap.TryGetValue(typeRef, out var result))
            {
                return result;
            }

            result = ResolveCore(typeRef);
            return _typeRefToTypeMap.GetOrAdd(typeRef, result);
        }

        protected abstract Type? ResolveCore(TypeRef typeRef);
    }
}
