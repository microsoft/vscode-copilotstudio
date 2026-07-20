namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio
{
    internal sealed class CreateGlobalVariableCommandArgs
    {
        public required string DocumentUri { get; init; }

        public required string VariableName { get; init; }

        public required string NewFileUri { get; init; }

        public required string FileContent { get; init; }

        public SetVariableInsertion? SetVariable { get; init; }
    }

    internal sealed class SetVariableInsertion
    {
        public required int Line { get; init; }

        public required int Character { get; init; }

        public required string TextBeforeValue { get; init; }

        public required string TextAfterValue { get; init; }
    }
}
