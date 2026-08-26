namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio;
    using System;
    using System.IO;
    using System.Linq;
    using Xunit;

    public class GetRemoteFileHandlerTests
    {
        private const string Bot = "crf9a_nagentn1_T2U1EY";
        private const string SkillSchema = Bot + ".skill.get-us-weather_e1W";
        private const string AssetSchema = Bot + ".file.scriptsgetusweatherps1_GNNvS";
        private const string AssetDisplayName = "scripts/Get-UsWeather.ps1";
        private const string NestedAssetPath = "behaviors/get-us-weather/scripts/Get-UsWeather.ps1.mcs.yml";

        private static readonly Guid SkillId = new("11111111-1111-1111-1111-111111111111");
        private static readonly Guid AssetId = new("22222222-2222-2222-2222-222222222222");

        private const string CloudCacheJson = $$"""
        {
          "$kind": "BotDefinition",
          "entity": {
            "$kind": "BotEntity",
            "schemaName": "{{Bot}}",
            "displayName": "NAgent N1",
            "template": "cliagent-1.0.0",
            "configuration": {
              "$kind": "BotConfiguration",
              "recognizer": { "$kind": "CLICopilotRecognizer" },
              "agentSettings": {
                "$kind": "AgentSettings",
                "instructions": { "$kind": "Instructions" }
              }
            }
          },
          "components": [
            {
              "$kind": "DialogComponent",
              "id": "11111111-1111-1111-1111-111111111111",
              "schemaName": "{{SkillSchema}}",
              "displayName": "get-us-weather",
              "dialog": "kind: InlineAgentSkill\ncontent: placeholder\n"
            },
            {
              "$kind": "FileAttachmentComponent",
              "id": "22222222-2222-2222-2222-222222222222",
              "parentBotComponentId": "11111111-1111-1111-1111-111111111111",
              "schemaName": "{{AssetSchema}}",
              "displayName": "{{AssetDisplayName}}"
            }
          ]
        }
        """;

        /// <summary>
        /// A remote update to a single nested asset carries neither the agent entity nor the parent
        /// skill, so resolving the preview from the delta alone selects the wrong projection rule
        /// and the diff shows a file that does not match the one on disk.
        /// </summary>
        [Fact]
        public void BuildRemoteCloudImage_IsolatedAssetUpdate_ResolvesNestedSkillPathFromCachedCloudImage()
        {
            RunWithCachedWorkspace(workspace =>
            {
                var updatedAsset = CreateAsset();
                var changeSet = new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentUpdate(updatedAsset) }, bot: null, changeToken: "token-2");

                var image = GetRemoteFileHandler.BuildRemoteCloudImage(workspace, changeSet);

                var botImage = Assert.IsType<BotDefinition>(image);
                Assert.NotNull(botImage.Entity);
                Assert.Contains(image.Components.OfType<DialogComponent>(), component => component.Dialog is InlineAgentSkill);

                var path = new LspComponentPathResolver().GetComponentPath(updatedAsset, image);
                Assert.Equal(NestedAssetPath, path.Replace('\\', '/'));
            });
        }

        /// <summary>
        /// Pins the regression: a definition built only from the delta loses the entity and the
        /// parent skill, which is what produced the wrong preview shape.
        /// </summary>
        [Fact]
        public void BuildRemoteCloudImage_DeltaOnlyDefinition_DoesNotResolveNestedSkillPath()
        {
            var updatedAsset = CreateAsset();
            var deltaOnly = new BotDefinition().WithComponents(new BotComponentBase[] { updatedAsset });

            var path = new LspComponentPathResolver().GetComponentPath(updatedAsset, deltaOnly);

            Assert.NotEqual(NestedAssetPath, path.Replace('\\', '/'));
        }

        /// <summary>
        /// The cloud cache is an image of the cloud, so building a preview must neither write to it
        /// nor mutate the document model the workspace holds for it.
        /// </summary>
        [Fact]
        public void BuildRemoteCloudImage_DoesNotMutateCachedCloudDefinition()
        {
            RunWithCachedWorkspace((workspace, cachePath) =>
            {
                var schemaNamesBefore = SchemaNames(ReadCachedDefinition(workspace));
                var bytesBefore = File.ReadAllBytes(cachePath);

                var addedAsset = CreateAsset("scripts/New-Asset.ps1", Guid.NewGuid(), $"{Bot}.file.newasset_a1B");
                var changeSet = new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentUpdate(addedAsset) }, bot: null, changeToken: "token-2");

                var image = GetRemoteFileHandler.BuildRemoteCloudImage(workspace, changeSet);

                var cachedAfter = ReadCachedDefinition(workspace);
                Assert.Equal(schemaNamesBefore, SchemaNames(cachedAfter));
                Assert.DoesNotContain(cachedAfter.Components, component => component.SchemaNameString == addedAsset.SchemaNameString);
                Assert.Equal(bytesBefore, File.ReadAllBytes(cachePath));
                Assert.Contains(image.Components, component => component.SchemaNameString == addedAsset.SchemaNameString);
            });
        }

        private static DefinitionBase ReadCachedDefinition(McsWorkspace workspace)
        {
            var document = workspace.GetDocument(workspace.FolderPath.GetChildFilePath(".mcs/botdefinition.json"));
            return (DefinitionBase)((McsLspDocument)document!).FileModel!;
        }

        private static string[] SchemaNames(DefinitionBase definition)
            => definition.Components.Select(component => component.SchemaNameString).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        private static void RunWithCachedWorkspace(Action<McsWorkspace> assert)
            => RunWithCachedWorkspace((workspace, _) => assert(workspace));

        private static void RunWithCachedWorkspace(Action<McsWorkspace, string> assert)
        {
            var source = Path.GetFullPath(Path.Combine("TestData", "Workspace", "NestedSkillWorkspace"));
            var destination = Path.Combine(Path.GetTempPath(), "remote-preview-" + Guid.NewGuid().ToString("N"));
            CopyDirectory(source, destination);

            try
            {
                var cacheDirectory = Path.Combine(destination, ".mcs");
                Directory.CreateDirectory(cacheDirectory);
                var cachePath = Path.Combine(cacheDirectory, "botdefinition.json");
                File.WriteAllText(cachePath, CloudCacheJson);

                var world = new World(destination);
                var workspace = world.GetWorkspace();
                workspace.BuildCompilationModel();

                assert(workspace, cachePath);
            }
            finally
            {
                Directory.Delete(destination, recursive: true);
            }
        }

        private static FileAttachmentComponent CreateAsset(string displayName = AssetDisplayName, Guid? id = null, string schemaName = AssetSchema)
        {
            var builder = new FileAttachmentComponent()
                .WithSchemaName(schemaName)
                .WithDisplayName(displayName)
                .ToBuilder();
            builder.Id = id ?? AssetId;
            builder.ParentBotComponentId = new BotComponentId(SkillId);
            return builder.Build();
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }

            foreach (var directory in Directory.EnumerateDirectories(source))
            {
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }
    }
}
