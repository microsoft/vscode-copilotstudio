namespace Microsoft.PowerPlatformLS.Impl.Language.Yaml.Validation
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Validation;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.Yaml.Model;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using Range = Microsoft.PowerPlatformLS.Contracts.Lsp.Models.Range;

    internal sealed class WorkflowStateValidationRule : IValidationRule<YamlLspDocument>
    {
        private const string WorkflowNotEnabledCode = "WorkflowNotEnabled";
        private const int ActivatedState = 2;
        private static readonly AgentFilePath ConnectionsCachePath = new(".mcs/.connections-cache.json");
        private static readonly JsonSerializerOptions CacheSerializerOptions = new(JsonSerializerDefaults.Web);
        private readonly IFileAccessorFactory _fileAccessorFactory;

        public WorkflowStateValidationRule(IFileAccessorFactory fileAccessorFactory)
        {
            _fileAccessorFactory = fileAccessorFactory ?? throw new ArgumentNullException(nameof(fileAccessorFactory));
        }

        IEnumerable<Diagnostic> IValidationRule<YamlLspDocument>.ComputeValidation(RequestContext context, YamlLspDocument document)
        {
            if (document == null)
            {
                yield break;
            }

            var normalizedPath = document.FilePath.ToString().Replace('\\', '/');
            if (!normalizedPath.EndsWith("/metadata.yml", StringComparison.OrdinalIgnoreCase) || normalizedPath.IndexOf("/workflows/", StringComparison.OrdinalIgnoreCase) < 0)
            {
                yield break;
            }

            var workspace = context.Workspace;
            if (workspace == null)
            {
                yield break;
            }

            var workflow = FindWorkflow(workspace.FolderPath, normalizedPath);
            if (workflow == null || workflow.State == ActivatedState)
            {
                yield break;
            }

            var displayName = string.IsNullOrEmpty(workflow.DisplayName) ? "(unnamed)" : workflow.DisplayName;
            yield return new Diagnostic
            {
                Code = WorkflowNotEnabledCode,
                Range = new Range
                {
                    Start = new Position { Line = 0, Character = 0 },
                    End = new Position { Line = 0, Character = 0 },
                },
                Severity = DiagnosticSeverity.Warning,
                Message = $"Workflow '{displayName}' is in Draft and will not run until enabled. Open Connection Manager to enable it.",
            };
        }

        private WorkflowDto? FindWorkflow(DirectoryPath folderPath, string normalizedDocumentPath)
        {
            try
            {
                var accessor = _fileAccessorFactory.Create(folderPath);
                if (!accessor.Exists(ConnectionsCachePath))
                {
                    return null;
                }

                string json;
                using (var stream = accessor.OpenRead(ConnectionsCachePath))
                using (var reader = new StreamReader(stream))
                {
                    json = reader.ReadToEnd();
                }

                var parsed = JsonSerializer.Deserialize<CacheFileDto>(json, CacheSerializerOptions);
                if (parsed?.Workflows == null)
                {
                    return null;
                }

                foreach (var workflow in parsed.Workflows)
                {
                    var filePath = workflow?.FilePath;
                    if (string.IsNullOrEmpty(filePath))
                    {
                        continue;
                    }

                    var normalizedWorkflowPath = filePath!.Replace('\\', '/');
                    if (normalizedDocumentPath.EndsWith(normalizedWorkflowPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return workflow;
                    }
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private sealed class CacheFileDto
        {
            public List<WorkflowDto>? Workflows { get; set; }
        }

        private sealed class WorkflowDto
        {
            public string? DisplayName { get; set; }

            public string? FilePath { get; set; }

            public int State { get; set; }
        }
    }
}
