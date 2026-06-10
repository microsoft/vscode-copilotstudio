namespace Microsoft.CopilotStudio.McsCore
{
    /// <summary>
    /// Persisted authoring/schema shape of an agent - the schema lineage that the
    /// future declared authoring-model signal identifies. This is the discriminator
    /// the merge actually cares about; it is deliberately distinct from
    /// <see cref="WorkspaceLayout"/> (the local file projection) and from runtime
    /// behavior / recognizer / template values.
    /// </summary>
    /// <remarks>
    /// More shapes are expected over time. Consumers should <c>switch</c> on this with
    /// an explicit default branch rather than assuming a CLI-vs-classic binary.
    /// Unrecognized shapes resolve to <see cref="Unknown"/> and preserve their raw
    /// declared/evidence value on <see cref="AgentClassification.RawShapeValue"/>.
    /// </remarks>
    public enum AuthoringShape
    {
        Unknown = 0,

        Classic = 1,

        CliCopilot = 2,
    }

    /// <summary>
    /// Local workspace file-projection family. A separate concept from
    /// <see cref="AuthoringShape"/>: it describes how files are laid out on disk, not
    /// the persisted schema lineage. The CLI layered layout is keyed off the canonical
    /// <c>agent.yaml</c> identity file; the classic layout is keyed off
    /// <c>settings.mcs.yml</c>.
    /// </summary>
    public enum WorkspaceLayout
    {
        Unknown = 0,

        ClassicMcs = 1,

        CliLayered = 2,
    }

    /// <summary>
    /// Graded support level. Not a binary gate: an unrecognized-but-well-formed agent
    /// is <see cref="Provisional"/> rather than fully blocked, because the shared object
    /// format lets non-destructive operations (clone, pull) work best-effort so a new
    /// authoring shape can be bootstrapped. Destructive operations still require
    /// <see cref="Supported"/>.
    /// </summary>
    public enum SupportLevel
    {
        /// <summary>Read-only inspection of safe metadata only.</summary>
        Unsupported = 0,

        /// <summary>Recognized object format but not a recognized authoring shape:
        /// clone/pull best-effort allowed (bootstrap); push/reattach fail closed.</summary>
        Provisional = 1,

        /// <summary>Recognized authoring shape: all operations allowed.</summary>
        Supported = 2,
    }

    /// <summary>
    /// Sync operations, ordered by cloud impact, used to evaluate the per-operation
    /// support gate.
    /// </summary>
    public enum SyncOperation
    {
        /// <summary>Read-only inspection. Always allowed (safe metadata).</summary>
        Inspect = 0,

        /// <summary>Cloud -> new local workspace. Non-destructive to cloud.</summary>
        Clone = 1,

        /// <summary>Cloud -> existing local workspace. Non-destructive to cloud.</summary>
        Pull = 2,

        /// <summary>Local -> cloud. Destructive to cloud.</summary>
        Push = 3,

        /// <summary>Create / reattach a cloud agent. Destructive to cloud.</summary>
        Reattach = 4,
    }

    /// <summary>
    /// Single structured classification result (PRD R1, TDD D1). Separates the persisted
    /// authoring shape from the local workspace layout, carries a graded
    /// <see cref="SupportLevel"/>, preserves the raw value of unrecognized shapes, and
    /// records the evidence that produced the result.
    /// </summary>
    public readonly record struct AgentClassification(
        AuthoringShape AuthoringShape,
        WorkspaceLayout WorkspaceLayout,
        SupportLevel Support,
        string? RawShapeValue,
        string? Evidence)
    {
        /// <summary>No agent / no usable evidence.</summary>
        public static readonly AgentClassification None =
            new(AuthoringShape.Unknown, WorkspaceLayout.Unknown, SupportLevel.Unsupported, null, "no-agent");

        /// <summary>
        /// Per-operation support gate (the "flexible fail-closed"). Inspect is always
        /// allowed; clone/pull need at least <see cref="SupportLevel.Provisional"/> so an
        /// unrecognized shape can be bootstrapped; push/reattach require
        /// <see cref="SupportLevel.Supported"/> to protect the cloud.
        /// </summary>
        public bool Allows(SyncOperation operation) => operation switch
        {
            SyncOperation.Inspect => true,
            SyncOperation.Clone or SyncOperation.Pull =>
                Support is SupportLevel.Supported or SupportLevel.Provisional,
            SyncOperation.Push or SyncOperation.Reattach =>
                Support is SupportLevel.Supported,
            _ => false,
        };

        internal static AgentClassification Recognized(
            AuthoringShape shape, WorkspaceLayout layout, string evidence)
            => new(shape, layout, SupportLevel.Supported, null, evidence);

        internal static AgentClassification Provisional(
            string? rawShapeValue, WorkspaceLayout layout, string evidence)
            => new(AuthoringShape.Unknown, layout, SupportLevel.Provisional, rawShapeValue, evidence);

        internal AgentClassification WithLayout(WorkspaceLayout layout)
            => this with { WorkspaceLayout = layout };
    }
}
