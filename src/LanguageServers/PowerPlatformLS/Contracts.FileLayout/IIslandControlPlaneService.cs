namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using System.Threading;
    using System.Threading.Tasks;

    // This must be public so that it can be created from DI.
    // And public types must live in a Contracts.* assembly. 
    public interface IIslandControlPlaneService
    {
        void SetIslandBaseEndpoint(string baseEndpoint);

        Task<PvaComponentChangeSet> GetComponentsAsync(AuthoringOperationContextBase operationContext, string? changeToken, CancellationToken cancellationToken);

        Task<PvaComponentChangeSet> SaveChangesAsync(AuthoringOperationContextBase operationContext, PvaComponentChangeSet pushChangeset, CancellationToken cancellationToken);
    }
}