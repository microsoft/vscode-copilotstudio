import { unescape } from "querystring";
import { CancellationToken, commands, EventEmitter, ExtensionContext, scm, SourceControlResourceGroup, Uri, window, workspace } from "vscode";
import { addWorkspaceChangeSubscription, CopilotStudioWorkspace, getAllWorkspaces, updateWorkspaceCache, hasConnectionFileInWorkspace } from "./localWorkspaces";
import { LocalChangeResourceCommandResolver, RemoteChangeResourceCommandResolver, Resource, ResourceCommandResolver } from "./changeTracking";
import { SyncResponse, Change, SyncRequest, DiffRequest } from "../types";
import { LOCAL_STATE_SCHEME } from "./originalState";
import { getOrAddSynchronizer, SyncState } from "./workspaceSynchronizer";
import { getKnowledgeLocalChanges, getKnowledgeRemoteChanges } from "../knowledgeFiles/syncUtils";
import { registerVirtualKnowledgeProvider } from "../knowledgeFiles/virtualKnowledgeFile";
import { lspClient, buildLspRequestPayload } from '../services/lspClient';
import { isChildUri, isSameUri } from "../utils/genericUtils";
import { LspMethods, TelemetryEventsKeys } from "../constants";
import logger from "../services/logger";
import { refreshAgentChangesTree } from "./agentChangesTreeProvider";

interface WorkspaceScm {
  workspace: CopilotStudioWorkspace;
  onLocalChange: () => Promise<void>;
  onRemoteChange: () => Promise<void>;
  getLocalChanges: () => Resource[];
  getRemoteChanges: () => Resource[];
  dispose: () => void;
}

const workspaceMap: Map<string, WorkspaceScm> = new Map<string, WorkspaceScm>();
// Tracks in-flight setup promises keyed by workspace URI to avoid concurrent initialization races
const workspaceSetupPromises: Map<string, Promise<void>> = new Map<string, Promise<void>>();

/**
 * Get the local and remote changes for a workspace.
 * Used by the Agent Changes tree view.
 */
export function getWorkspaceChanges(workspaceUri: string): { localChanges: Resource[]; remoteChanges: Resource[] } | undefined {
  const scmInstance = workspaceMap.get(workspaceUri);
  if (!scmInstance) {
    return undefined;
  }
  return {
    localChanges: scmInstance.getLocalChanges(),
    remoteChanges: scmInstance.getRemoteChanges(),
  };
}

/**
 * Refresh the remote changes after a fetch operation.
 * Called by Apply command to ensure we have up-to-date remote state.
 */
export async function refreshAgentChangesAfterFetch(workspaceUri: string): Promise<void> {
  const scmInstance = workspaceMap.get(workspaceUri);
  if (scmInstance) {
    await scmInstance.onRemoteChange();
    refreshAgentChangesTree();
  }
}

export function onWorkspaceChange(uri: string): void {
  // Find the matching workspace by checking if the uri is a child of any workspace uri
  for (const [workspaceUri, scm] of workspaceMap.entries()) {
    if (isChildUri(uri, workspaceUri)) {
      // Fire-and-forget; we intentionally don't await to avoid blocking file events.
      // Errors are caught to prevent unhandled promise rejections.
      void scm.onLocalChange()
        .then(() => refreshAgentChangesTree())
        .catch(err => {
          logger.logError(TelemetryEventsKeys.SyncWorkspaceError, undefined, {
            message: `[SCM] onLocalChange failed: ${(err as Error).message}`
          });
        });
      return;
    }
  }
}

export function initializeWorkspaceScm(context: ExtensionContext) {
  addWorkspaceChangeSubscription((workspaces) => {
    refreshWorkspaces(workspaces, context);
  });
  refreshWorkspaces(getAllWorkspaces(), context);

  // Listen for configuration changes to toggle SCM/Agent Changes view mode
  // NOTE: useAgentChangesView setting has been removed. This listener is unreachable.
  // To-deprecate at later date: This entire configuration change listener.
  /* context.subscriptions.push(
    workspace.onDidChangeConfiguration(async (e) => {
      if (e.affectsConfiguration('ms-CopilotStudio.useAgentChangesView')) {
        // Wait for any in-flight setup promises to complete first
        await Promise.allSettled(Array.from(workspaceSetupPromises.values()));
        // Now dispose all existing workspace SCMs and clear the map
        for (const [uri, scmInstance] of workspaceMap.entries()) {
          scmInstance.dispose();
        }
        workspaceMap.clear();
        // Re-setup all workspaces with new configuration
        await refreshWorkspaces(getAllWorkspaces(), context);
        // Refresh the Agent Changes tree to update visibility
        refreshAgentChangesTree();
      }
    })
  ); */
}

export async function refreshWorkspaces(workspaces: CopilotStudioWorkspace[], context: ExtensionContext) {
  const desiredUris = new Set(workspaces.map(w => w.workspaceUri));
  const newPromises: Promise<void>[] = [];
  // Precompute connection file availability (case-insensitive path on Windows)
  const eligibility = workspaces.map(ws => ({
    ws,
    hasConn: hasConnectionFileInWorkspace(ws.workspaceUri)
  }));
  for (const ws of workspaces) {
    const uri = ws.workspaceUri;
    const entry = eligibility.find(e => e.ws === ws);
    const hasConn = entry?.hasConn;
    if (
      !workspaceMap.has(uri) &&
      !workspaceSetupPromises.has(uri) &&
      ws.syncInfo &&
      ws.syncInfo.agentManagementEndpoint &&
      hasConn
    ) {
        const setupPromise = (async () => {
          try {
            const tracking = await setupChangeTracking(ws, context);
            if (desiredUris.has(uri)) {
              workspaceMap.set(uri, tracking);
            } else {
              tracking.dispose();
            }
          } finally {
            workspaceSetupPromises.delete(uri);
          }
        })();
        workspaceSetupPromises.set(uri, setupPromise);
        newPromises.push(setupPromise);
    }
  }

  if (newPromises.length) {
    // await on newPromises rather than all workspaceSetupPromises to make this independent of other refreshWorkspaces calls.
    // we might wish to change this in the future if we want to ensure all workspaces are fully initialized before any refresh completes.
    const results = await Promise.allSettled(newPromises);
    for (const r of results) {
      if (r.status === 'rejected') {
        const reason = r.reason instanceof Error ? r.reason.message : String(r.reason);
        logger.logError(TelemetryEventsKeys.SyncWorkspaceError, undefined, {
          message: `[SCM] Workspace setup failed: ${reason}`
        });
      }
    }
  }

  for (const [uri, scmInstance] of workspaceMap.entries()) {
    if (!desiredUris.has(uri)) {
      scmInstance.dispose();
      workspaceMap.delete(uri);
    }
  }

  // Update context key for Agent Changes view visibility
  const hasConnectedAgent = workspaceMap.size > 0;
  void commands.executeCommand('setContext', 'mcs.hasConnectedAgent', hasConnectedAgent);
}

export async function pushNewWorkspace(context: ExtensionContext, ws: CopilotStudioWorkspace) {
  const updatedCache = await updateWorkspaceCache(ws);
  await refreshWorkspaces(updatedCache, context);

  const synchronizer = getOrAddSynchronizer(ws);
  const virtualKnowledgeProvider = await registerVirtualKnowledgeProvider(context, ws);
  await synchronizer.pull(virtualKnowledgeProvider);
  await synchronizer.push();
  await synchronizer.pull(virtualKnowledgeProvider);
}

async function setupChangeTracking(ws: CopilotStudioWorkspace, context: ExtensionContext): Promise<WorkspaceScm> {
  const { displayName, syncInfo, workspaceUri } = ws;

  // Check if Agent Changes view is enabled - if so, don't create SCM UI
  // NOTE: useAgentChangesView setting has been removed. Always use Agent Changes view (never create SCM).
  // To-deprecate at later date: This check and all related SCM view creation code.
  // const useAgentChangesView = workspace.getConfiguration('ms-CopilotStudio').get<boolean>('useAgentChangesView', true);
  const useAgentChangesView = true; // Hardcoded: setting removed, always use Agent Changes view

  // Only create SCM view when Agent Changes view is disabled (classic SCM mode)
  const scmView = useAgentChangesView ? undefined : scm.createSourceControl('mcs', 'Copilot Studio - ' + displayName);
  
  // Hoisted so they can be disposed if initialization fails
  let fileDecorationChangeEmitter: EventEmitter<Uri[]> | undefined;
  let provider: { dispose(): void } | undefined;
  
  // Internal storage for changes (used when SCM is not created)
  let localChangesStore: Resource[] = [];
  let remoteChangesStore: Resource[] = [];
  
  try { // Outer try to log any unexpected error before promise rejection propagates

  if (scmView) {
    // Classic SCM mode - set up SCM UI
    // NOTE: This code path is unreachable as useAgentChangesView setting is deprecated and defaults to true.
    // To-deprecate at later date: This entire SCM view and its commands.
    scmView.acceptInputCommand = { command: 'microsoft-copilot-studio.syncPush', title: 'Push', arguments: [ws] };
    scmView.inputBox.visible = false;
    scmView.statusBarCommands = [
      { command: 'microsoft-copilot-studio.syncPush', title: "$(repo-push)", tooltip: "Push changes to Copilot Studio", arguments: [ws] },
      { command: 'microsoft-copilot-studio.syncPull', title: "$(repo-pull)", tooltip: "Pull changes from Copilot Studio", arguments: [ws] },
      { command: 'microsoft-copilot-studio.syncFetch', title: "$(git-fetch)", tooltip: "Fetch changes from Copilot Studio", arguments: [ws] },
    ];
  }

  // Create SCM resource groups only when SCM view exists
  const localChangeGroup = scmView ? createScmView('local', 'Local Changes', ws) : undefined;
  const remoteChangeGroup = scmView ? createScmView('remote', 'Remote Changes', ws) : undefined;
  const localCommandController = new LocalChangeResourceCommandResolver(Uri.parse(workspaceUri));
  const remoteCommandController = new RemoteChangeResourceCommandResolver(Uri.parse(workspaceUri));

  const fetchChanges = async<TInput>(requestType: string, input: TInput): Promise<Change[]> => {
    try {
      const result = await lspClient.sendRequest<SyncResponse>(requestType, input);
      return result.localChanges;
    } catch (error) {
      return [];
    }
  };

  fileDecorationChangeEmitter = new EventEmitter<Uri[]>();
  let lastFileAnnotations: Uri[] = [];
  provider = window.registerFileDecorationProvider({
    onDidChangeFileDecorations: fileDecorationChangeEmitter.event,
    provideFileDecoration: (uri: Uri) => {
      // Use internal store when SCM is not created, otherwise use SCM resource states
      const resources = localChangeGroup ? localChangeGroup.resourceStates : localChangesStore;
      const resource = resources.find((r: any) => isSameUri((r as Resource).fullResourceUri, uri));
      if (resource) {
        return (resource as Resource).resourceDecoration;
      }
      return undefined;
    }
  });
  context.subscriptions.push(provider);

  const schemaNames = new Map<string, string>();
  
  // Only set up quick diff when SCM view exists
  if (scmView) {
    scmView.quickDiffProvider = {
      provideOriginalResource(uri: Uri, token: CancellationToken) {
        const normalizedUri = unescape(uri.toString(true).toLowerCase());
        const schema = schemaNames.get(normalizedUri);
        if (!schema) {
          return undefined;
        }

        return Uri.from({ scheme: LOCAL_STATE_SCHEME, authority: "local", path: "/" + schema, query: workspaceUri });
      },
    };
  }

  // Tracks if any remote change fetch has succeeded for this workspace instance
  let remoteHadSuccess = false;

  const result: WorkspaceScm = {
    workspace: ws,
    onLocalChange: async () => {
      if (!syncInfo || !syncInfo.dataverseEndpoint) {
        return;
      }
      try {
        const diffRequest: DiffRequest = {
          ...await buildLspRequestPayload(syncInfo),
          workspaceUri
        };
        const generalChanges = await fetchChanges<DiffRequest>(LspMethods.GET_LOCAL_CHANGES, diffRequest);
        const knowledgeChanges = await getKnowledgeLocalChanges(syncInfo, workspaceUri);
        const allChanges = [...generalChanges, ...knowledgeChanges];
        const resources = mapResources(allChanges, localCommandController);
        
        // Store changes in SCM resource group or internal store
        if (localChangeGroup) {
          localChangeGroup.resourceStates = resources;
        } else {
          localChangesStore = resources;
        }

        schemaNames.clear();
        resources.forEach(element => {
          schemaNames.set(unescape(element.fullResourceUri.toString(true).toLowerCase()), element.schemaName);
        });

        const newUris = resources.map(r => r.fullResourceUri);
        const previousChanges = lastFileAnnotations;
        const changedUris = newUris.concat(previousChanges);
        lastFileAnnotations = newUris;
        fileDecorationChangeEmitter?.fire(changedUris);
      } catch (e) {
        logger.logError(TelemetryEventsKeys.SyncWorkspaceError, undefined, {
          message: `onLocalChangeError: ${e instanceof Error ? e.message : String(e)}`
        });
        throw e;
      }
    },
    onRemoteChange: async () => {
      if (!syncInfo || !syncInfo.dataverseEndpoint) {
        return;
      }
      // Track per-instance remote success to decide whether to log anomalies (3)
      // We leverage closure variable remoteHadSuccess
      try {
        const request: SyncRequest = {
          ...await buildLspRequestPayload(syncInfo),
          workspaceUri
        };
        const remoteChanges = await fetchChanges<SyncRequest>(LspMethods.GET_REMOTE_CHANGES, request);
        const knowledgeChanges = await getKnowledgeRemoteChanges(syncInfo, workspaceUri);
        const allRemoteChanges = [...remoteChanges, ...knowledgeChanges];
        const resources = mapResources(allRemoteChanges, remoteCommandController);
        
        // Store changes in SCM resource group or internal store
        if (remoteChangeGroup) {
          remoteChangeGroup.resourceStates = resources;
        } else {
          remoteChangesStore = resources;
        }
        remoteHadSuccess = true;
      } catch (e) {
        if (remoteHadSuccess) {
          logger.logError(TelemetryEventsKeys.SyncWorkspaceError, undefined, {
            message: `onRemoteChangeErrorAfterSuccess: ${e instanceof Error ? e.message : String(e)}`
          });
        }
        // Swallow to avoid aborting setup; remote can retry later.
      }
    },
    getLocalChanges: () => {
      return localChangeGroup ? localChangeGroup.resourceStates as Resource[] : localChangesStore;
    },
    getRemoteChanges: () => {
      return remoteChangeGroup ? remoteChangeGroup.resourceStates as Resource[] : remoteChangesStore;
    },
    dispose: () => {
      localChangeGroup?.dispose();
      remoteChangeGroup?.dispose();
      scmView?.dispose();
      fileDecorationChangeEmitter?.dispose();
      provider?.dispose();
    }
  };

  const synchronizer = getOrAddSynchronizer(ws);
  let lastOperation: SyncState = SyncState.Idle;
  synchronizer.subscribe(async (state) => {
    if (state === SyncState.Idle) {
      if (lastOperation === SyncState.Pulling || lastOperation === SyncState.Pushing) {
        await result.onRemoteChange();
        await result.onLocalChange();
        const remoteChanges = result.getRemoteChanges();
        if (remoteChanges.length > 0) {
          // Unexpected clearing with remaining remote items (4)
          logger.logError(TelemetryEventsKeys.SyncWorkspaceError, undefined, {
            message: 'clearingRemoteWithRemainingChanges'
          });
        }
        // Clear after sync operations that reconcile state
        if (remoteChangeGroup) {
          remoteChangeGroup.resourceStates = [];
        } else {
          remoteChangesStore = [];
        }
        refreshAgentChangesTree();
      } else if (lastOperation === SyncState.Fetching) {
        // Note: Because we dont emit the sync results; we end up re-fetching the changes
        await result.onRemoteChange();
        refreshAgentChangesTree();
      }
    }

    lastOperation = state;
  });

  await result.onLocalChange();
  try { await result.onRemoteChange(); } catch { /* swallow */ }
  refreshAgentChangesTree();
  if (scmView) {
    context.subscriptions.push(scmView);
  }
  context.subscriptions.push(result);
  if (fileDecorationChangeEmitter) { 
    context.subscriptions.push(fileDecorationChangeEmitter); 
  }
  return result;
  } catch (e) {
    logger.logError(TelemetryEventsKeys.SyncWorkspaceError, undefined, {
      message: `setupChangeTrackingRejected: ${e instanceof Error ? e.message : String(e)}`
    });
    // Dispose any partially created resources
    try {
      provider?.dispose();
      fileDecorationChangeEmitter?.dispose();
      scmView?.dispose();
    } catch { /* ignore */ }
    throw e; // propagate to caller so top-level catch logs too
  }

  function mapResources(changes: Change[], commandController: ResourceCommandResolver): Resource[] {
    return changes.map<Resource>(s => new Resource(
      commandController,
      Uri.parse(s.uri),
      s.schemaName,
      s.changeKind,
      s.changeType));
  }

  function createScmView(id: string, name: string, ws: CopilotStudioWorkspace): SourceControlResourceGroup & { ws: CopilotStudioWorkspace } {
    const rg = scmView!.createResourceGroup(id, name) as SourceControlResourceGroup & { ws: CopilotStudioWorkspace };
    rg.ws = ws;
    return rg;
  }
}
