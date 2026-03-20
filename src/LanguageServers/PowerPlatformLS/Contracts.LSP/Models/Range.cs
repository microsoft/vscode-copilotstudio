
namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;

    [DebuggerDisplay("{Start}-{End}")]
    public readonly struct Range
    {
        public static Range Zero { get; } = new Range { Start = new Position { Line = 0, Character = 0 }, End = new Position { Line = 0, Character = 0 } };

        public Position Start { get; init; }

        public Position End { get; init; }

        public override string ToString()
        {
            return $"{Start}-{End}";
        }
    }
}