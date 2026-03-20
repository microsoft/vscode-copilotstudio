namespace Microsoft.PowerPlatformLS.Impl.Core.IO
{
    using Microsoft.Extensions.FileProviders;

    internal interface IFileProviderFactory
    {
        IFileProvider Create(string root);
    }
}
