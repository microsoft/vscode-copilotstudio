// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Validation;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Xunit;

    public class EnvironmentVariableValidationTests
    {
        private const string AgentSchemaName = "cree9_agent";
        private const string SharedSchemaName = "cr9f2_shared";

        public static TheoryData<Func<EnvironmentVariableDefinition, EnvironmentVariableDefinition>> Edits => new()
        {
            environmentVariable => environmentVariable.WithDisplayName("Edited"),
            environmentVariable => environmentVariable.WithDefaultValue("edited"),
            environmentVariable => environmentVariable.WithValueComponent(new EnvironmentVariableValue(value: "edited")),
            environmentVariable => environmentVariable.WithType(EnvironmentVariableType.Number),
        };

        [Theory]
        [MemberData(nameof(Edits))]
        public void EditedSharedEnvironmentVariable_ReportsNoErrors(Func<EnvironmentVariableDefinition, EnvironmentVariableDefinition> edit)
        {
            var cloud = CreateEnvironmentVariable(SharedSchemaName);

            Assert.DoesNotContain(Validate(new[] { cloud }, edit(cloud)), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        }

        [Fact]
        public void SharedEnvironmentVariableEditedToHaveNoValue_ReportsAnError()
        {
            var cloud = CreateEnvironmentVariable(SharedSchemaName);

            Assert.Contains(Validate(new[] { cloud }, cloud.WithDefaultValue(string.Empty).WithValueComponent(null)), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        }

        private static EnvironmentVariableDefinition CreateEnvironmentVariable(string schemaName) => new(
            displayName: schemaName,
            schemaName: schemaName,
            id: new EnvironmentVariableDefinitionId(Guid.NewGuid()),
            defaultValue: "one",
            valueComponent: new EnvironmentVariableValue(id: Guid.NewGuid(), value: "current"),
            type: EnvironmentVariableType.String);

        private static IReadOnlyList<Diagnostic> Validate(IEnumerable<EnvironmentVariableDefinition> cloudEnvironmentVariables, EnvironmentVariableDefinition local)
        {
            using var directory = new TempDirectory();
            File.WriteAllText(Path.Combine(directory.Path, "settings.mcs.yml"), $"schemaName: {AgentSchemaName}\n");
            Directory.CreateDirectory(Path.Combine(directory.Path, ".mcs"));
            WorkspaceSynchronizer.WriteCloudCache(new FileAccessorFactory().Create(new DirectoryPath(directory.Path.Replace('\\', '/') + "/")), new BotDefinition().WithEntity(new BotEntity.Builder { SchemaName = new BotEntitySchemaName(AgentSchemaName) }.Build()).WithEnvironmentVariables(cloudEnvironmentVariables));
            var path = Path.Combine(Directory.CreateDirectory(Path.Combine(directory.Path, "environmentvariables")).FullName, local.SchemaName.Value + ".mcs.yml");
            using (var writer = new StringWriter())
            {
                CodeSerializer.Serialize(writer, local);
                File.WriteAllText(path, writer.ToString());
            }

            var world = new World(directory.Path);
            var document = world.GetDocument(path) ?? throw new Xunit.Sdk.XunitException("Expected the environment variable document.");
            world.GetWorkspace().BuildCompilationModel();
            return world.GetRequiredServices<IValidationRule<McsLspDocument>>().SelectMany(rule => rule.ComputeValidation(world.GetRequestContext(document, 0), document)).ToList();
        }
    }
}
