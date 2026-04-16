namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Exceptions;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;

    // Find references. 
    internal interface IReferenceResolver
    {
        /// <summary>
        /// Resolve a Component Collection or throw.
        /// </summary>
        /// <param name="workspacePath">Current directory that the references.mcs.yml lives in</param>
        /// <param name="componentRef">Reference to resolve. This points to a target directory.</param>
        /// <exception cref="McsException">Thrown if we can't resolve the reference.</exception>
        /// <returns></returns>
        BotComponentCollectionDefinition ResolveComponentCollectionOrThrow(
            DirectoryPath workspacePath,
            ReferenceItemSourceFile componentRef);
    }
        
    internal class McsReferenceResolver : IReferenceResolver
    {
        private readonly ILanguageAbstraction _language;
        private readonly IClientWorkspaceFileProvider _fileProvider;

        public McsReferenceResolver(ILanguageAbstraction language, IClientWorkspaceFileProvider analyzer)
        {
            _language = language;
            _fileProvider = analyzer;   
        }

        public BotComponentCollectionDefinition ResolveComponentCollectionOrThrow(
            DirectoryPath workspacePath,
            ReferenceItemSourceFile componentRef)
        {
            var path = componentRef.Directory;
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new BadReferenceException($"Missing directory", componentRef);
            }

            // full directory for the target component collection. 
            DirectoryPath fullpath = workspacePath.ResolveRelativeRef(new RelativeDirectoryPath(path));

            BotComponentCollection? target = null;
            McsWorkspace? mcsWorkspace = null;

            // Check language abstraction to see if already loaded.
            // This avoids disk operations. 
            foreach (var existingWorkspace in _language.Workspaces.OfType<McsWorkspace>())
            {
                if (existingWorkspace.FolderPath.Equals(fullpath))
                {
                    // Workspace already exists.
                    // File is parsed, but still do validation checks below before compiling. 
                    var doc = existingWorkspace.GetDocument(LspProjectionLayout.CollectionMcsYml);
                    if (doc is McsLspDocument doc2)
                    {
                        if (doc2.FileModel is BotComponentCollection x)
                        {
                            mcsWorkspace = existingWorkspace;
                            target = x;
                        }
                    }
                    break;
                }
            }

            // Not in the cache, read from disk

            // The full path to the collection.mcs.yml file definining the cc. 
            FilePath ccFile = fullpath.GetChildFilePath(LspProjectionLayout.CollectionMcsYml.ToString());

            if (target == null)
            {
                var fileInfo = _fileProvider.GetFileInfo(ccFile);
                if (!fileInfo.Exists)
                {
                    // missing file!
                    throw new BadReferenceException($"File does not exist: {ccFile}", componentRef);
                }

                var fileText = fileInfo.ReadAllText();

                target = CodeSerializer.Deserialize<BotComponentCollection>(fileText);


                if (target == null)
                {
                    // Missing
                    throw new BadReferenceException($"File can't be deserialized: {ccFile}", componentRef);
                }
            }

            if (componentRef.SchemaName.HasValue && (componentRef.SchemaName != target.SchemaName))
            {                
                throw new BadReferenceException($"Schema mismatch: expected '{componentRef.SchemaName}', but {ccFile} is actually '{target.SchemaName}'", componentRef);
            }

            // Done basic validation. Safe to load rest of workspace. 

            // This will load files and potentiall compile.
            // Checks above are crucial to ensure compilation is sufficiently safe and
            // does not cause infinite recursion.
            if (mcsWorkspace == null)
            {
                var ws = _language.ResolveWorkspace(fullpath);
                mcsWorkspace = (McsWorkspace)ws;
            }

            // The workspace is already built, so we can skip calling
            // mcsWorkspace.BuildCompilationModel();

            // This should always be true if we passed the checks above. 
            if (mcsWorkspace.Definition is BotComponentCollectionDefinition definition)
            {
                return definition;
            }

            throw new BadReferenceException($"Workspace isn't a Component Collection: {ccFile}", componentRef);
        }
    }
}
