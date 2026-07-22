// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using System.IO;
    using System.Text;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using Xunit;

    public class ChildAgentLinkTests
    {
        private const string BotName = "crf9a_AgentE4Child";

        [Fact]
        public void ParseAndSerialize_RoundTrip()
        {
            var json = ChildAgentLink.Serialize(new ChildAgentLink.LinkData { SchemaName = $"{BotName}.agent.Agent", FolderName = "Agent Child 1" });

            var parsed = ChildAgentLink.Parse(json);

            Assert.NotNull(parsed);
            Assert.Equal($"{BotName}.agent.Agent", parsed!.SchemaName);
            Assert.Equal("Agent Child 1", parsed.FolderName);
        }

        [Fact]
        public void ReadSchemaLinks_MapsFolderNameToLinkedSchema()
        {
            var accessor = new InMemoryFileWriter();
            WriteChildAgent(accessor, "Agent Child 1", $"{BotName}.agent.Agent");
            WriteChildAgent(accessor, "Agent Child 2", $"{BotName}.agent.Agent_BR2");

            var links = ChildAgentLink.ReadSchemaLinks(accessor);

            Assert.Equal(2, links.Count);
            Assert.Equal($"{BotName}.agent.Agent", links["Agent Child 1"]);
            Assert.Equal($"{BotName}.agent.Agent_BR2", links["Agent Child 2"]);
        }

        [Fact]
        public void ReadSchemaLinks_FolderWithoutLink_IsOmitted()
        {
            var accessor = new InMemoryFileWriter();
            WriteChildAgent(accessor, "Agent Child 1", $"{BotName}.agent.Agent");
            WriteAgentDefinition(accessor, "Agent Child 2");

            var links = ChildAgentLink.ReadSchemaLinks(accessor);

            Assert.True(links.ContainsKey("Agent Child 1"));
            Assert.False(links.ContainsKey("Agent Child 2"));
        }

        [Fact]
        public void ReadSchemaLinks_MalformedLink_IsOmittedWithoutThrowing()
        {
            var accessor = new InMemoryFileWriter();
            WriteAgentDefinition(accessor, "Agent Child 1");
            WriteText(accessor, "agents/Agent Child 1/.agent.json", "{ this is not valid json");

            var links = ChildAgentLink.ReadSchemaLinks(accessor);

            Assert.Empty(links);
        }

        [Fact]
        public void ReadSchemaLinks_EmptySchema_IsOmitted()
        {
            var accessor = new InMemoryFileWriter();
            WriteChildAgent(accessor, "Agent Child 1", schemaName: string.Empty);

            var links = ChildAgentLink.ReadSchemaLinks(accessor);

            Assert.Empty(links);
        }

        [Fact]
        public void ReadSchemaLinks_NoChildAgents_ReturnsEmpty()
        {
            var accessor = new InMemoryFileWriter();

            var links = ChildAgentLink.ReadSchemaLinks(accessor);

            Assert.Empty(links);
        }

        [Fact]
        public void ReadSchemaLinks_LinkBesideNonAgentFile_IsIgnored()
        {
            var accessor = new InMemoryFileWriter();
            WriteChildAgent(accessor, "Agent Child 1", $"{BotName}.agent.Agent");
            WriteText(accessor, "agents/Agent Child 1/actions/Foo.mcs.yml", "kind: TaskDialog\n");

            var links = ChildAgentLink.ReadSchemaLinks(accessor);

            Assert.Single(links);
            Assert.Equal($"{BotName}.agent.Agent", links["Agent Child 1"]);
        }

        private static void WriteChildAgent(InMemoryFileWriter accessor, string folderName, string schemaName)
        {
            WriteAgentDefinition(accessor, folderName);
            var link = ChildAgentLink.Serialize(new ChildAgentLink.LinkData { SchemaName = schemaName, FolderName = folderName });
            WriteText(accessor, $"agents/{folderName}/.agent.json", link);
        }

        private static void WriteAgentDefinition(InMemoryFileWriter accessor, string folderName)
        {
            WriteText(accessor, $"agents/{folderName}/agent.mcs.yml", $"mcs.metadata:\n  componentName: {folderName}\nkind: AgentDialog\nbeginDialog:\n  kind: OnToolSelected\n  id: main\n");
        }

        private static void WriteText(InMemoryFileWriter accessor, string path, string content)
        {
            using var stream = accessor.OpenWrite(new AgentFilePath(path));
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(content);
        }
    }
}
