import { commands, DiagnosticSeverity, ExtensionContext, languages, ProgressLocation, RelativePattern, TextDocument, Uri, window, workspace as VSworkspace } from "vscode";
import { CopilotStudioWorkspace, getAllWorkspaces, hasConnectionFileInWorkspace } from "../sync/localWorkspaces";
import { selectWorkspace } from "../sync/workspacePicker";
import { getOrAddSynchronizer, WorkspaceSynchronizer } from "../sync/workspaceSynchronizer";
import { registerVirtualKnowledgeProvider } from "../knowledgeFiles/virtualKnowledgeFile";
import { getWorkspaceChanges, refreshAgentChangesAfterFetch } from "../sync/workspaceScm";
import { TelemetryEventsKeys } from "../constants";
import logger from "../services/logger";

type Workspace = { ws: CopilotStudioWorkspace } | CopilotStudioWorkspace | null;

interface SyncCommand {
  id: string;
  displayName: string;
  action: (synchroniser: WorkspaceSynchronizer) => Promise<void>;
}

export const registerSyncCommands = (context: ExtensionContext) => {
  const syncCommands: SyncCommand[] = [

    /*  We are deprecating the old SCM mode commands in favor of the Agent Changes view commands
        TODO: Remove the old commands entirely in a future release
    {
      id: 'microsoft-copilot-studio.syncPull',
      displayName: 'Pull',
      action: async (synchroniser) => {
        const virtualKnowledgeProvider = await registerVirtualKnowledgeProvider(context, synchroniser.workspace);
        await synchroniser.pull(virtualKnowledgeProvider);
      }
    },
    {
      id: 'microsoft-copilot-studio.syncPush',
      displayName: 'Push',
      action: async (synchroniser) => { await synchroniser.push(); }
    },
    {
      id: 'microsoft-copilot-studio.syncFetch',
      displayName: 'Fetch',
      action: async (synchroniser) => { await synchroniser.fetch(); }
    },*/
    // Agent Changes view commands (delegate to existing sync operations)
    {
      id: 'microsoft-copilot-studio.previewChanges',
      displayName: 'Preview',
      action: async (synchroniser) => { await synchroniser.fetch(); }
    },
    {
      id: 'microsoft-copilot-studio.getChanges',
      displayName: 'Get',
      action: async (synchroniser) => {
        const virtualKnowledgeProvider = await registerVirtualKnowledgeProvider(context, synchroniser.workspace);
        await synchroniser.pull(virtualKnowledgeProvider);
      }
    },
    {
      id: 'microsoft-copilot-studio.applyChanges',
      displayName: 'Apply',
      action: async (synchroniser) => { await synchroniser.push(); }
    }
  ];

  syncCommands.forEach(command => registerSyncCommand(context, command));
};

// Checks .mcs.yml/.mcs.yaml files in the workspace and returns any diagnostics errors found
const getDiagnosticsErrors = async (workspace: CopilotStudioWorkspace) => {
  let files = 0;
  let count = 0;
  const workspaceUri = Uri.parse(workspace.workspaceUri);
  const yamlFiles = await VSworkspace.findFiles(
    new RelativePattern(workspaceUri, '**/*.mcs.{yml,yaml}')
  );

  // Open all files to ensure diagnostics are calculated
  const openedDocuments: TextDocument[] = [];
  for (const fileUri of yamlFiles) {
    try {
      const document = await VSworkspace.openTextDocument(fileUri);
      openedDocuments.push(document);
    } catch (error) {
      logger.logError(TelemetryEventsKeys.SyncWorkspaceError, undefined, { message: `Error opening file <pii>${fileUri.fsPath}</pii>: ${(error as Error).message}` });
    }
  }

  // Give the language server a moment to calculate diagnostics
  // Only necessary for files that weren't previously open
  // Note: Implementing pull diagnostics methods from LSP 3.17 could remove the need for this delay in the future
  // https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#workspace_diagnostic
  // https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#textDocument_diagnostic
  if (openedDocuments.length > 0) {
    await new Promise(resolve => setTimeout(resolve, 1000));
  }

  for (const fileUri of yamlFiles) {
    const diagnostics = languages.getDiagnostics(fileUri);
    const diagnosticErrors = diagnostics.filter(diagnostic => diagnostic.severity === DiagnosticSeverity.Error);

    if (diagnosticErrors.length > 0) {
      files++;
      count += diagnosticErrors.length;
    }
  }

  return { files, count };
};

const registerSyncCommand = (
  context: ExtensionContext,
  { id, displayName, action }: SyncCommand
) => {
  const syncCommand = commands.registerCommand(id, async (workspace?: Workspace) => {
    try {
      logger.logInfo(TelemetryEventsKeys.SyncWorkspaceClick);
      const selectedWorkspace = workspace && typeof workspace === 'object' && 'ws' in workspace && workspace.ws
        ? workspace.ws
        : await selectWorkspace();

      if (!selectedWorkspace) {
        const workspaces = getAllWorkspaces();
        if (workspaces.length > 0) {
          logger.logWarning(TelemetryEventsKeys.SyncWorkspaceCancel, `No workspace selected. ${displayName} operation cancelled.`);
        } else {
          logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `No workspace found for ${displayName} operation`);
        }
        return;
      }

      if (selectedWorkspace && !selectedWorkspace.syncInfo) {
        if (hasConnectionFileInWorkspace(selectedWorkspace.workspaceUri)) {
          logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `Cannot perform ${displayName} operation: connection settings in .mcs::conn.json are incomplete or invalid, please clone again.`);
        } else {
          logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `Cannot perform ${displayName} operation: connection file .mcs::conn.json is missing, please clone again.`);
        }
        return;
      }

      let errors = { files: 0, count: 0 };

      // For Apply operation, fetch remote changes first then check for conflicts
      if (id === 'microsoft-copilot-studio.applyChanges') {
        // First, fetch to get the current remote state
        await window.withProgress({
          location: ProgressLocation.Notification,
          title: 'Checking for remote changes...',
          cancellable: false
        }, async () => {
          const synchronizer = getOrAddSynchronizer(selectedWorkspace);
          await synchronizer.fetch();
          // Trigger refresh to update the change stores
          await refreshAgentChangesAfterFetch(selectedWorkspace.workspaceUri);
        });

        // Now check if there are remote changes
        const changes = getWorkspaceChanges(selectedWorkspace.workspaceUri);
        // Filter out knowledge file changes - Knowledge file change is optional and do not block ApplyChanges.
        const changesWithoutKnowledgeFiles =
          changes?.remoteChanges.filter(
            r => r.changeKind !== 'knowledge'
          ) ?? [];

        if (changesWithoutKnowledgeFiles.length > 0) {
          const remoteCount = changesWithoutKnowledgeFiles.length;
          const choice = await window.showWarningMessage(
            `The agent has ${remoteCount} remote change${remoteCount === 1 ? '' : 's'} that must be retrieved before your local changes can be applied.`,
            { modal: true },
            'Get Remote Changes'
          );

          if (choice === 'Get Remote Changes') {
            // Execute Get command instead
            await commands.executeCommand('microsoft-copilot-studio.getChanges', { ws: selectedWorkspace });
          }
          // Either way, don't proceed with Apply
          return;
        }
      }

      await window.withProgress({
        location: ProgressLocation.Notification,
        title: `${displayName} operation in progress. Please wait...`,
        cancellable: false
      }, async () => {
        // For Push/Apply operations, check for errors before proceeding
        if (id === 'microsoft-copilot-studio.syncPush' || id === 'microsoft-copilot-studio.applyChanges') {
          errors = await getDiagnosticsErrors(selectedWorkspace);
        }
        if (errors.count === 0) {
          const synchronizer = getOrAddSynchronizer(selectedWorkspace);
          await action(synchronizer);
        }
      });

      if ((id === 'microsoft-copilot-studio.syncPush' || id === 'microsoft-copilot-studio.applyChanges') && errors.count > 0) {
        const errorMessage = `Cannot perform ${displayName.toLowerCase()} operation: found ${errors.count} error(s) in ${errors.files} file(s).`;
        logger.logWarning(TelemetryEventsKeys.SyncWorkspaceError, undefined, { message: errorMessage });
        const detailView = await window.showErrorMessage(errorMessage, 'View Details');

        // Navigate to Problems view if user selects 'View Details'
        if (detailView === 'View Details') {
          await commands.executeCommand('workbench.actions.view.problems');
        }
      }
    } catch (error) {
      logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `Failed to execute ${displayName} operation: ${(error as Error).message}`);
    }
  });

  context.subscriptions.push(syncCommand);
};
