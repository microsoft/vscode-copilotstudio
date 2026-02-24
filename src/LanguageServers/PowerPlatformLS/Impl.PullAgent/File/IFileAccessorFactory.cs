namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;

    internal interface IFileAccessorFactory
    {
        IFileAccessor Create(DirectoryPath root);
    }
}
