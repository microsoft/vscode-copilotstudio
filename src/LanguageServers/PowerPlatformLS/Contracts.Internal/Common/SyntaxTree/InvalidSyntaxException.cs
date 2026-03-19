namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common.SyntaxTree
{
    using System;

    [Serializable]
    public class InvalidSyntaxException : Exception
    {
        public MarkRange Range { get; }

        public InvalidSyntaxException(Exception innerException, MarkRange range) : base(innerException.Message, innerException)
        {
            Range = range;
        }
    }
}