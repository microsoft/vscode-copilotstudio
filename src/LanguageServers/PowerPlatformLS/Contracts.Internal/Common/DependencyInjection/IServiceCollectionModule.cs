namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection
{
    using Microsoft.Extensions.DependencyInjection;

    /// <summary>
    /// A module to help with re-using configured bindings.
    /// </summary>
    public interface IServiceCollectionModule
    {
        /// <summary>
        /// Set up bindings for the service collection.
        /// </summary>
        /// <param name="services">The current service collection.</param>
        void ConfigureServices(IServiceCollection services);
    }
}
