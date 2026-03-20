namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Resources
{
    using Microsoft.Agents.ObjectModel;
    using Schema = Microsoft.Agents.ObjectModel.Schema;

    internal interface IStringResources
    {
        StringResource GetElementDescription(BotElementKind kind);
        StringResource GetEnumDescription(Schema.PrimitiveKind primitiveKind);
        StringResource GetEnumMemberDescription(Schema.PrimitiveKind primitiveKind, string member);
        StringResource GetPropertyDescription(BotElementKind kind, string propertyName);
    }

}
