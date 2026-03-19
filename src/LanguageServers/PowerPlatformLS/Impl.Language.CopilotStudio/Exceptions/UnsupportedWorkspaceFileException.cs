namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Exceptions
{
    using System;

    internal class UnsupportedWorkspaceFileException : Exception
    {
        private const string GenericErrorMessage = "Copilot Studio files must be organized in a workspace folder. Please open a folder with appropriate file structure. See documentation at: https://github.com/microsoft/vscode-copilotstudio (placeholder).";

        public UnsupportedWorkspaceFileException(string fileInfo)
            : base($"{GenericErrorMessage}\n{fileInfo}")
        {
        }
    }
}
