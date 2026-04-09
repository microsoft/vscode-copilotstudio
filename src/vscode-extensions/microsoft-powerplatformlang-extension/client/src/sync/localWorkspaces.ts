import * as path from 'path';
import * as fs from 'fs';
import { ExtensionContext, IconPath, ThemeIcon, Uri, workspace } from "vscode";
import { Disposable } from "vscode-languageclient";
import { lspClient } from '../services/lspClient';
import { AgentSyncInfo } from "../types";
import { getIcon } from "../icon";
import { getClusterCategory, isChildUri } from '../utils/genericUtils';
import { getEnvironmentByIdAsync } from '../clients/bapClient';
import { onAccountChange } from '../clients/account';
import { LspMethods } from '../constants';
import logger from '../services/logger';
import { TelemetryEventsKeys } from '../constants';

export type  WorkspaceIcon = IconPath | ThemeIcon;

export enum WorkspaceType {
  Unknown = -1,
  Agent = 0,
  ComponentCollection = 1,
}

export interface CopilotStudioWorkspace {
  workspaceUri: string;
  displayName: string;
  description: string;
  icon: WorkspaceIcon;
  type: WorkspaceType
  syncInfo?: AgentSyncInfo
}

interface ListWorkspacesResponse {
  workspaceUris: string[];
}

interface WorkspaceDetailsResponse extends CopilotStudioWorkspace {
  IconFilePath?: string;
}

type WorkspaceChangeCallback = (workspaces: CopilotStudioWorkspace[]) => void;

const callbacks: WorkspaceChangeCallback[] = [];
let workspaceCache: CopilotStudioWorkspace[] = [];
let refreshPending = false;
let refreshInProgress = false;
const MAX_REFRESH_ITERATIONS = 3;
let iterations = 0;

// Per-session cache of workspaces where endpoint repair was attempted and failed.
// Prevents repeated BAP calls on every workspace refresh.
// Cleared on auth/session changes so a sign-in can retry.
const repairAttempted = new Set<string>();

export const getAllWorkspaces = (): CopilotStudioWorkspace[] => workspaceCache;

// Finds the matching workspace by checking if the uri is a child of any workspace URI.
export const findWorkspaceForUri = (uri: string): CopilotStudioWorkspace | undefined => workspaceCache.find(workspace =>
  isChildUri(uri, workspace.workspaceUri)
);

// Finds a workspace that contains the specified URI.
export const getWorkspaceByUri = (uri: Uri): CopilotStudioWorkspace | undefined => {
  const uriString = uri.scheme === 'mcs' ? decodeURIComponent(uri.query) : uri.toString();
  return workspaceCache.find(workspace => uriString.startsWith(decodeURI(workspace.workspaceUri)));
};

// Checks if .mcs/conn.json exists in the workspace
export const hasConnectionFileInWorkspace = (workspaceUri: string): boolean => {
  const workspaceFolder = Uri.parse(workspaceUri).fsPath;
  const connFilePath = path.join(workspaceFolder, '.mcs', 'conn.json');
  return fs.existsSync(connFilePath);
};

export async function initializeLocalWorkspaces(context: ExtensionContext) {
  // Clear endpoint repair negative cache on auth changes so sign-in can retry.
  context.subscriptions.push(await onAccountChange(() => repairAttempted.clear()));

  const allFileWatcher = workspace.createFileSystemWatcher('**/*.*');
  allFileWatcher.onDidChange(async (uri) => {
    const loweredPath = uri.path.toLowerCase();
    if (loweredPath.endsWith('.mcs/conn.json')
      || loweredPath.endsWith('.mcs/botdefinition.json')
      || loweredPath.endsWith('icon.png')
      || loweredPath.endsWith('settings.mcs.yml')
      || loweredPath.endsWith('collection.mcs.yml')) {
      if (loweredPath.endsWith('.mcs/conn.json')) {
        repairAttempted.clear(); // conn.json changed — allow repair to re-run.
      }
      refreshAndNotify();
    }
  });

  context.subscriptions.push(workspace.onDidOpenTextDocument(e => {
    e.uri.scheme === 'file' && refreshAndNotify();
  }));

  context.subscriptions.push(
    workspace.onDidChangeWorkspaceFolders(() => {
      refreshAndNotify();
    })
  );

  refreshAndNotify();
}

export function addWorkspaceChangeSubscription(callback: WorkspaceChangeCallback): Disposable {
  callbacks.push(callback);
  const disposable = Disposable.create(() => { callbacks.splice(callbacks.indexOf(callback), 1); });
  return disposable;
}

export async function updateWorkspaceCache(ws: CopilotStudioWorkspace): Promise<CopilotStudioWorkspace[]> {
  const existingIndex = workspaceCache.findIndex(w => w.workspaceUri === ws.workspaceUri);
  if (existingIndex !== -1) {
    workspaceCache[existingIndex] = ws;
  } else {
    workspaceCache.push(ws);
  }

  await refreshAndNotify();
  return workspaceCache;
}

async function refreshAndNotify() {
  refreshPending = true;

  if (refreshInProgress) {
    // Refresh already in progress, will refresh again when done
    return;
  }

  refreshInProgress = true;

  try {
    while (refreshPending) {
      if (iterations++ >= MAX_REFRESH_ITERATIONS) {
        logger.logInfo(TelemetryEventsKeys.WorkspaceRefreshLoopCap);
        break;
      }
      refreshPending = false;
      workspaceCache = await listWorkspaces();

      // Notify subscribers (they might call refreshAndNotify again; that just sets refreshPending=true).
      for (const callback of callbacks) {
        callback(workspaceCache);
      }
    }
  } finally {
    refreshInProgress = false;
    iterations = 0;
  }
}

async function listWorkspaces(): Promise<CopilotStudioWorkspace[]> {
  try {
    const workspaces: CopilotStudioWorkspace[] = [];
    const response = await lspClient.sendRequest<ListWorkspacesResponse>(LspMethods.LIST_WORKSPACES);
    for (const workspaceUri of response.workspaceUris) {
      const data = await lspClient.sendRequest<WorkspaceDetailsResponse>(LspMethods.GET_WORKSPACE_DETAILS, { workspaceUri });
      data.icon = getWorkspaceIcon(data.type, data.IconFilePath);
      workspaces.push(data);
    }
    return workspaces;
  } catch (error) {
    return [];
  }
}

/**
 * Attempts to resolve a missing agentManagementEndpoint from the BAP single-environment API.
 * PAC-cloned workspaces may have a null endpoint when the user lacks PP admin role for the
 * admin-scoped BAP list. The single-environment lookup (GET /environments/{id}) may succeed
 * where the admin list fails.
 *
 * If resolved, updates syncInfo in place and rewrites conn.json to disk.
 * Returns true if the endpoint was repaired.
 */
export async function tryRepairAgentManagementEndpoint(syncInfo: AgentSyncInfo, workspaceUri: string): Promise<boolean> {
  if (syncInfo.agentManagementEndpoint) {
    return true; // Already present, nothing to repair.
  }

  if (!syncInfo.environmentId || !syncInfo.accountInfo || repairAttempted.has(workspaceUri)) {
    return false;
  }

  try {
    const clusterCategory = getClusterCategory(syncInfo.accountInfo);
    const envInfo = await getEnvironmentByIdAsync(clusterCategory, syncInfo.environmentId, null);
    if (envInfo?.agentManagementUrl) {
      syncInfo.agentManagementEndpoint = envInfo.agentManagementUrl;

      // Persist the repair to conn.json. The file watcher will trigger refreshAndNotify(),
      // which re-enters the workspace into the cache with the repaired endpoint, causing
      // SCM and Agent Changes filters to pick it up.
      const workspaceFolder = Uri.parse(workspaceUri).fsPath;
      const connFilePath = path.join(workspaceFolder, '.mcs', 'conn.json');
      if (fs.existsSync(connFilePath)) {
        const connData = JSON.parse(fs.readFileSync(connFilePath, 'utf-8'));
        connData.AgentManagementEndpoint = envInfo.agentManagementUrl;
        fs.writeFileSync(connFilePath, JSON.stringify(connData), 'utf-8');
      }

      return true;
    }
  } catch {
    // BAP single-environment lookup failed — fall through to existing error.
  }

  repairAttempted.add(workspaceUri); // Don't retry until auth or conn.json changes.
  return false;
}

function getWorkspaceIcon(workspaceType: WorkspaceType, iconFilePath?: string): WorkspaceIcon {
  if (iconFilePath) {
    const iconFileUri = Uri.file(iconFilePath);
    return { light: iconFileUri, dark: iconFileUri };
  }
  switch (workspaceType) {
    case WorkspaceType.Agent:
      return getIcon();
    case WorkspaceType.ComponentCollection:
      return new ThemeIcon("package");
    default:
      return new ThemeIcon("folder");
  }
}
