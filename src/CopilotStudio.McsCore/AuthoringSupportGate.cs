namespace Microsoft.CopilotStudio.McsCore
{
    using System;

    /// <summary>
    /// Production enforcement of the per-operation support gate (TDD D35). Wraps
    /// <see cref="AgentClassification.Allows(SyncOperation)"/> so the destructive sync
    /// handlers (push, reattach) fail closed for an unrecognized/provisional authoring
    /// shape, with a single actionable message. The gate logic is isolated here so it is
    /// unit-testable independent of the heavy pull-agent handler DI: callers pass the
    /// classification (built via <see cref="AgentClassifier.Classify(Agents.ObjectModel.BotEntity?, string?)"/>)
    /// rather than the handler context.
    /// </summary>
    /// <remarks>
    /// Clone and pull are non-destructive and remain permissive (they allow
    /// <see cref="SupportLevel.Provisional"/> so a new shape can be bootstrapped, D4/R8);
    /// only push and reattach require <see cref="SupportLevel.Supported"/>.
    /// </remarks>
    public static class AuthoringSupportGate
    {
        /// <summary>
        /// Builds an actionable message describing why <paramref name="operation"/> is
        /// blocked for the given <paramref name="classification"/>, including the support
        /// level, resolved shape, and any preserved raw shape value, plus the available
        /// non-destructive alternatives.
        /// </summary>
        public static string DescribeBlocked(AgentClassification classification, SyncOperation operation)
        {
            var rawSuffix = string.IsNullOrEmpty(classification.RawShapeValue)
                ? string.Empty
                : $", raw shape '{classification.RawShapeValue}'";

            return
                $"The {operation} operation is blocked because this agent's authoring shape is not recognized as fully " +
                $"supported (support level '{classification.Support}', shape '{classification.AuthoringShape}'{rawSuffix}). " +
                "Push and reattach require a recognized authoring shape to protect the cloud agent; clone and pull remain " +
                "available to inspect or update the local copy.";
        }

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> when <paramref name="operation"/>
        /// is not allowed for the given <paramref name="classification"/>. The sync handlers
        /// map this to a 400 user error. No-op when the operation is allowed.
        /// </summary>
        public static void EnsureAllowed(AgentClassification classification, SyncOperation operation)
        {
            if (!classification.Allows(operation))
            {
                throw new InvalidOperationException(DescribeBlocked(classification, operation));
            }
        }
    }
}
