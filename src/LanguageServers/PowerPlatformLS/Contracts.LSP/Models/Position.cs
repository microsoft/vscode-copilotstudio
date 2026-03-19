
﻿namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public readonly struct Position
    {
        public int Line { get; init; }

        public int Character { get; init; }

        public override string ToString()
        {
            return $"{Line}:{Character}";
        }
    }
}