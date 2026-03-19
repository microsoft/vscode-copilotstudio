
namespace Microsoft.PowerPlatformLS.Contracts.Internal
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public static class Constants
    {
        public const string Null = "null";
        public static class CompletionItemPriority
        {
            public const string P0 = "0";
            public const string P1 = "1";
            public const string P2 = "2";
            public const string P3 = "3";
            public const string P4 = "4";
        }

        /// <summary>
        /// CLaSP (Roslyn's Common Language Server Protocol) works with string whereas we prefer to work with enums.
        /// </summary>
        public static class LanguageIds
        {
            public static readonly string CopilotStudio = LanguageType.CopilotStudio.ToString();
            public static readonly string PowerFx = LanguageType.PowerFx.ToString();
            public static readonly string Yaml = LanguageType.Yaml.ToString();
        }

        public static readonly JsonSerializerOptions DefaultSerializationOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Allows matching JSON properties without case sensitivity.
            PropertyNameCaseInsensitive = true,
            // Ignores null values when writing JSON (optional).
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull | JsonIgnoreCondition.WhenWritingDefault,
            Converters =
            {
                // Custom converter for Uri deserialization.
                new DocumentUriConverter(),
                new FileOperationConverter()
            }
        };

        public static readonly Diagnostic UnknownSemanticErrorDiagnostic = new Diagnostic
        {
            Range = Range.Zero,
            Severity = DiagnosticSeverity.Error,
            Message = $"Internal Error. Failed to compute semantic model."
        };

        public static class ErrorCodes
        {
            public const string WrongLocationForEntityType = "WrongLocationForEntityType";
        }

        public static class JsonRpcMethods
        {
            public const string AgentDirectoryChange = "powerplatformls/onAgentDirectoryChange";
            public const string GetLocalChanges = "powerplatformls/getLocalChanges";
        }
    }
}