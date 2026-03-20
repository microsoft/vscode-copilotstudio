namespace Microsoft.PowerPlatformLS.Contracts.FileLayout
{
    using Microsoft.Agents.ObjectModel;

    /// <summary>
    /// Resolves file paths for ObjectModel components based on LS projection rules.
    /// </summary>
    public interface IComponentPathResolver
    {
        /// <summary>
        /// Gets the projected relative file path for a component.
        /// </summary>
        /// <param name="component">The component to resolve.</param>
        /// <param name="definition">Optional definition for context (bot/collection name, parent links).</param>
        string GetComponentPath(BotComponentBase component, DefinitionBase? definition = null);
    }
}
