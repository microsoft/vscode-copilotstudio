namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common.SyntaxTree
{
    /// <summary>
    /// This is our common interface for a file location.
    /// Helps bridge the gap between ObjectModel.Location, YamlDotNet.Core.Mark and LSP.Position.
    /// </summary>
    public class Mark
    {
        public int Line { get; init; }
        public int Column { get; init; }
    }
}
