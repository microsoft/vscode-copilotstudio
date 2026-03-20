namespace Microsoft.PowerPlatformLS.Contracts.Internal.Models
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using System;
    using System.Collections.Generic;

    public class Compilation<T>
    {
        public Compilation(T model, Dictionary<LspDocument, IEnumerable<Exception>> errors)
        {
            Model = model;
            Errors = errors;
        }

        public T Model { get; }
        public Dictionary<LspDocument, IEnumerable<Exception>> Errors { get; }
    }
}