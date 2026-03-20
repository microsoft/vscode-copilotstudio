namespace Microsoft.PowerPlatformLS.UnitTests
{
    using System;
    using System.Linq;
    using Xunit;

    public class AssemblyTests
    {
        /// <summary>
        /// Helps prevent oversights in types visibility.
        /// 
        /// This test uses reflection to make sure that architecture guidelines are followed.
        /// It is okay to make changes to this test but we should have a good reason and potentially update design guidelines accordinly: the LanguageServerArchitecture.md doc.
        /// </summary>
        [Fact]
        public void ImplementationLibraries_EncapsulationCheck()
        {
            var implAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.FullName?.StartsWith("Microsoft.PowerPlatformLS.Impl") == true)
                .OrderBy(x => x.GetName().Name)
                .ToArray();

            // Validate known assemblies are found exclusively
            string[] expectedAssemblies = [
                "Microsoft.PowerPlatformLS.Impl.Core",
                "Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio",
                "Microsoft.PowerPlatformLS.Impl.Language.PowerFx",
                "Microsoft.PowerPlatformLS.Impl.Language.Yaml",
                "Microsoft.PowerPlatformLS.Impl.PullAgent",
                ];
            TestUtilities.TestAssert.StringArrayEqual(expectedAssemblies, implAssemblies.Select(x => x.GetName().Name).ToArray());

            // Validate they all contain a single, known type
            var expectedVisibleTypes = new[]
            {
                "Microsoft.PowerPlatformLS.Impl.Core.DependencyInjection.HostApplicationBuilderExtensions",
                "Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.DependencyInjection.McsLspModule",
                "Microsoft.PowerPlatformLS.Impl.Language.PowerFx.DependencyInjection.PowerFxLspModule",
                "Microsoft.PowerPlatformLS.Impl.Language.Yaml.DependencyInjection.YamlLspModule",
                "Microsoft.PowerPlatformLS.Impl.PullAgent.DependencyInjection.PullAgentLspModule",
            };
            var visibleImplTypes = implAssemblies.SelectMany(x => x.GetExportedTypes()).Select(x => x.FullName).Order().ToArray();
            TestUtilities.TestAssert.StringArrayEqual(expectedVisibleTypes, visibleImplTypes);

            // Validate they don't reference one another
            foreach (var assembly in implAssemblies)
            {
                var referencedAssemblies = assembly.GetReferencedAssemblies();

                var problematicReferences = referencedAssemblies.Where(reference =>
                    implAssemblies.Any(a => a.FullName == reference.FullName));

                Assert.Empty(problematicReferences);
            }
        }
    }
}
