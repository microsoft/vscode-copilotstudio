namespace Microsoft.PowerPlatformLS.Impl.PullAgent.Dataverse
{
    using System.Text.RegularExpressions;

    /// <summary>
    /// Validates schema names for Dataverse agents.
    /// Original Copilot Studio's code on schema name:
    /// Original source: Copilot Studio schema name utility.
    /// </summary>
    internal static class SchemaNameValidator
    {
        // Regex: only allow alphanumeric characters and '_', '-', '.', '{', '}', '!'
        private static readonly Regex ValidSchemaNameRegex = new(@"^[A-Za-z0-9_\-\.{}!]+$", RegexOptions.Compiled);

        private const int MaxSchemaNameLength = 100;

        /// <summary>
        /// Check if schema name is syntactically valid.
        /// </summary>
        /// <param name="schemaName">Schema name to validate.</param>
        /// <returns>True if schema name is valid; otherwise, false.</returns>
        public static bool IsValid(string schemaName)
        {
            if (string.IsNullOrWhiteSpace(schemaName) || schemaName.Length > MaxSchemaNameLength)
            {
                return false;
            }

            return ValidSchemaNameRegex.IsMatch(schemaName);
        }
    }
}
