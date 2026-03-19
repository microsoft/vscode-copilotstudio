namespace Microsoft.PowerPlatformLS.Impl.Core.IO
{
    using Microsoft.Extensions.FileProviders;

    internal class PhysicalFileProviderFactory : IFileProviderFactory
    {
        public IFileProvider Create(string root)
        {
            return new PhysicalFileProvider(root);
        }
    }
}
