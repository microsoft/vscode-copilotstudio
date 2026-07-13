import { commands, EventEmitter, ExtensionContext, ThemeColor, ThemeIcon, TreeDataProvider, TreeItem, TreeItemCollapsibleState, TreeView, window, workspace } from "vscode";
import { addWorkspaceChangeSubscription, CopilotStudioWorkspace, getAllWorkspaces, getDuplicateDisplayNames, buildAgentIdentityTooltip, hasConnectionFileInWorkspace, WorkspaceType } from "./localWorkspaces";
import { Resource } from "./changeTracking";
import { getWorkspaceChanges } from "./workspaceScm";
import { ChangeType } from "../types";
import { getActiveSyncUri, getSyncStateFor, onAnySyncStateChanged, SyncState } from "./workspaceSynchronizer";
import { getAccountHealth, onAuthStateChanged, AccountHealth } from "../clients/account";
import { isAccountTokenUsable } from "../clients/bapClient";
import { getClusterCategory } from "../utils/genericUtils";

/**
 * Tree item types for the Agent Changes view hierarchy:
 * - Agent: Top-level node (one per connected workspace)
 * - ChangeGroup: "Local Changes" or "Remote Changes" group under each agent
 * - ChangeItem: Individual file change (placeholder for Phase 4)
 */
export enum AgentChangesItemKind {
  Agent = 1,
  ChangeGroup = 2,
  ChangeItem = 3,
}

export interface AgentTreeItem {
  kind: AgentChangesItemKind.Agent;
  workspace: CopilotStudioWorkspace;
}

export interface ChangeGroupTreeItem {
  kind: AgentChangesItemKind.ChangeGroup;
  workspace: CopilotStudioWorkspace;
  groupType: 'local' | 'remote';
  label: string;
}

export interface ChangeItemTreeItem {
  kind: AgentChangesItemKind.ChangeItem;
  workspace: CopilotStudioWorkspace;
  resource: Resource;
  groupType: 'local' | 'remote';
}

export type AgentChangesTreeItemUnion = AgentTreeItem | ChangeGroupTreeItem | ChangeItemTreeItem;

export function computeAgentAccountBadge(workspace: CopilotStudioWorkspace, isDuplicate: boolean): { description: string; health: AccountHealth } {
  const baseDescription = isDuplicate && workspace.schemaName ? workspace.schemaName : undefined;
  const accountInfo = workspace.syncInfo?.accountInfo;
  const health: AccountHealth = accountInfo ? getAccountHealth(accountInfo.accountId, accountInfo.accountEmail) : 'ok';
  const statusSuffix = health === 'terminal' ? 'account unavailable' : (health === 'signedOut' ? 'signed out' : undefined);
  const description = [baseDescription, accountInfo?.accountEmail, statusSuffix].filter(Boolean).join(' \u00b7 ');
  return { description, health };
}

export function isWorkspaceConnected(workspace: CopilotStudioWorkspace): boolean {
  return !!(workspace.syncInfo && workspace.syncInfo.agentManagementEndpoint && hasConnectionFileInWorkspace(workspace.workspaceUri));
}

export function describeDisconnection(workspace: CopilotStudioWorkspace): { message: string; action: 'signin' | 'reattach' } {
  if (!hasConnectionFileInWorkspace(workspace.workspaceUri)) {
    return { message: 'Not linked to a cloud agent \u2014 reattach to start syncing.', action: 'reattach' };
  }
  if (!workspace.syncInfo) {
    return { message: "Connection details couldn't be read \u2014 reattach to reconnect.", action: 'reattach' };
  }
  const account = workspace.syncInfo.accountInfo;
  const label = account?.accountEmail ?? account?.accountId ?? 'the linked account';
  const health = getAccountHealth(account?.accountId, account?.accountEmail);
  if (health === 'terminal') {
    return { message: `Can't sign in to ${label}.`, action: 'reattach' };
  }
  if (health === 'signedOut') {
    return { message: `Signed out \u2014 sign in to ${label}.`, action: 'signin' };
  }
  if (!workspace.syncInfo.agentManagementEndpoint) {
    return { message: `Not connected to its environment \u2014 sign in to ${label} to load cloud changes.`, action: 'signin' };
  }
  return { message: `Can't sign in to ${label}.`, action: 'signin' };
}

/**
 * Tree data provider for the Agent Changes view.
 * Displays a 3-level hierarchy:
 *   Level 1: Agent name (one per connected workspace)
 *   Level 2: "Local Changes" and "Remote Changes" groups
 *   Level 3: Changed files (Phase 4)
 */
class AgentChangesTreeDataProvider implements TreeDataProvider<AgentChangesTreeItemUnion> {
  private _onDidChangeTreeData = new EventEmitter<AgentChangesTreeItemUnion | undefined | void>();
  readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

  refresh(): void {
    this._onDidChangeTreeData.fire();
  }

  private accountUsable = new Map<string, boolean>();

  private accountKey(workspace: CopilotStudioWorkspace): string {
    const account = workspace.syncInfo?.accountInfo;
    return (account?.accountId || account?.accountEmail || '').toLowerCase();
  }

  private isConnectedForDisplay(workspace: CopilotStudioWorkspace): boolean {
    if (!isWorkspaceConnected(workspace)) {
      return false;
    }
    const key = this.accountKey(workspace);
    return key ? (this.accountUsable.get(key) ?? true) : true;
  }

  async probeAccountConnectivity(): Promise<void> {
    const seen = new Set<string>();
    let changed = false;
    for (const workspace of getAllWorkspaces()) {
      const account = workspace.syncInfo?.accountInfo;
      const key = this.accountKey(workspace);
      if (!account || !key || seen.has(key)) {
        continue;
      }
      seen.add(key);
      const usable = await isAccountTokenUsable(account.accountId, account.accountEmail, getClusterCategory(account));
      if (this.accountUsable.get(key) !== usable) {
        this.accountUsable.set(key, usable);
        changed = true;
      }
    }
    if (changed) {
      this.refresh();
    }
  }

  getTreeItem(element: AgentChangesTreeItemUnion): TreeItem {
    switch (element.kind) {
      case AgentChangesItemKind.Agent: {
        const isDuplicate = getDuplicateDisplayNames().has(element.workspace.displayName.toLowerCase());
        const badge = computeAgentAccountBadge(element.workspace, isDuplicate);
        const connected = this.isConnectedForDisplay(element.workspace);
        const item = new TreeItem(element.workspace.displayName, connected ? TreeItemCollapsibleState.Expanded : TreeItemCollapsibleState.None);
        item.id = `agent:${element.workspace.workspaceUri}`;
        if (connected) {
          item.description = badge.description;
          item.iconPath = badge.health === 'terminal'
            ? new ThemeIcon('error', new ThemeColor('list.errorForeground'))
            : badge.health === 'signedOut'
              ? new ThemeIcon('warning', new ThemeColor('list.warningForeground'))
              : element.workspace.icon;
        } else {
          item.description = [badge.description, 'not connected'].filter(Boolean).join(' \u00b7 ');
          item.iconPath = badge.health === 'terminal'
            ? new ThemeIcon('error', new ThemeColor('list.errorForeground'))
            : new ThemeIcon('debug-disconnect', new ThemeColor('list.warningForeground'));
        }
        item.tooltip = buildAgentIdentityTooltip(element.workspace, connected, connected ? undefined : describeDisconnection(element.workspace).message);
        item.contextValue = element.workspace.type === WorkspaceType.ComponentCollection ? 'componentCollection' : 'agent';
        return item;
      }
      case AgentChangesItemKind.ChangeGroup: {
        // Get the count of changes in this group
        const changes = getWorkspaceChanges(element.workspace.workspaceUri);
        const resources = changes 
          ? (element.groupType === 'local' ? changes.localChanges : changes.remoteChanges)
          : [];
        const count = resources.length;
        
        // Show count in label if there are changes
        const label = count > 0 ? `${element.label} (${count})` : element.label;
        
        // Expand if there are changes, collapse if empty
        const collapsibleState = count > 0 
          ? TreeItemCollapsibleState.Expanded 
          : TreeItemCollapsibleState.Collapsed;
        
        const item = new TreeItem(label, collapsibleState);
        item.id = `changeGroup:${element.workspace.workspaceUri}:${element.groupType}`;
        // Spinner mapping for the active sync workspace:
        //   Fetching / Pulling -> Remote Changes
        //   Pushing            -> Local Changes
        const syncState = getSyncStateFor(element.workspace.workspaceUri);
        const isBusy =
          (element.groupType === 'remote' && (syncState === SyncState.Fetching || syncState === SyncState.Pulling)) ||
          (element.groupType === 'local' && syncState === SyncState.Pushing);
        item.iconPath = isBusy
          ? new ThemeIcon('loading~spin')
          : new ThemeIcon(element.groupType === 'local' ? 'file-code' : 'cloud');
        item.contextValue = `changeGroup-${element.groupType}`;
        return item;
      }
      case AgentChangesItemKind.ChangeItem: {
        const resource = element.resource;
        const fileName = resource.resourceUri.path.split('/').pop() || resource.resourceUri.path;
        const item = new TreeItem(fileName, TreeItemCollapsibleState.None);
        item.id = `changeItem:${element.workspace.workspaceUri}:${element.groupType}:${resource.resourceUri.toString()}`;
        
        // Set icon based on change type
        item.iconPath = this.getChangeTypeIcon(resource.type);
        
        // Set description to show file path
        const pathParts = resource.resourceUri.path.split('/');
        if (pathParts.length > 1) {
          item.description = pathParts.slice(0, -1).join('/');
        }
        
        // Set tooltip
        item.tooltip = `${Resource.getStatusText(resource.type)}: ${resource.resourceUri.path}`;
        
        // Set context value for inline actions
        item.contextValue = `changeItem-${element.groupType}`;
        
        // Set command to open diff view when clicked
        item.command = resource.command;
        
        // Apply strikethrough for deleted files
        if (resource.type === ChangeType.Delete) {
          item.description = `${item.description || ''} (deleted)`;
        }
        
        return item;
      }
    }
  }

  /**
   * Get the appropriate icon for the change type.
   */
  private getChangeTypeIcon(changeType: ChangeType): ThemeIcon {
    switch (changeType) {
      case ChangeType.Create:
        return new ThemeIcon('diff-added', new ThemeColor('gitDecoration.addedResourceForeground'));
      case ChangeType.Delete:
        return new ThemeIcon('diff-removed', new ThemeColor('gitDecoration.deletedResourceForeground'));
      case ChangeType.Update:
      default:
        return new ThemeIcon('diff-modified', new ThemeColor('gitDecoration.modifiedResourceForeground'));
    }
  }

  /**
   * Return the parent of an element so `treeView.reveal` can locate it.
   * Hierarchy: Agent (root) -> ChangeGroup -> ChangeItem.
   */
  getParent(element: AgentChangesTreeItemUnion): AgentChangesTreeItemUnion | undefined {
    switch (element.kind) {
      case AgentChangesItemKind.Agent:
        return undefined;
      case AgentChangesItemKind.ChangeGroup:
        return { kind: AgentChangesItemKind.Agent, workspace: element.workspace };
      case AgentChangesItemKind.ChangeItem:
        return {
          kind: AgentChangesItemKind.ChangeGroup,
          workspace: element.workspace,
          groupType: element.groupType,
          label: element.groupType === 'local' ? 'Local Changes' : 'Remote Changes',
        };
    }
  }

  getChildren(element?: AgentChangesTreeItemUnion): AgentChangesTreeItemUnion[] {
    if (!element) {
      // Root level: return all connected/disconnected agents
      return getAllWorkspaces().map(ws => ({
        kind: AgentChangesItemKind.Agent,
        workspace: ws,
      }));
    }

    switch (element.kind) {
      case AgentChangesItemKind.Agent: {
        if (!this.isConnectedForDisplay(element.workspace)) {
          return [];
        }
        // Agent level: return Local and Remote change groups
        return [
          {
            kind: AgentChangesItemKind.ChangeGroup,
            workspace: element.workspace,
            groupType: 'local',
            label: 'Local Changes',
          },
          {
            kind: AgentChangesItemKind.ChangeGroup,
            workspace: element.workspace,
            groupType: 'remote',
            label: 'Remote Changes',
          },
        ];
      }
      case AgentChangesItemKind.ChangeGroup: {
        // Return actual change items from SCM
        const changes = getWorkspaceChanges(element.workspace.workspaceUri);
        if (!changes) {
          return [];
        }
        
        const resources = element.groupType === 'local' 
          ? changes.localChanges 
          : changes.remoteChanges;
        
        return resources.map(resource => ({
          kind: AgentChangesItemKind.ChangeItem,
          workspace: element.workspace,
          resource,
          groupType: element.groupType,
        }));
      }
      case AgentChangesItemKind.ChangeItem: {
        return [];
      }
    }
  }

  /**
   * Returns workspaces that have a connection file and syncInfo.
   * Uses the same criteria as workspaceScm.ts for SCM registration.
   */
  private getConnectedWorkspaces(): CopilotStudioWorkspace[] {
    return getAllWorkspaces().filter(isWorkspaceConnected);
  }

  /**
   * Get total local change count across all connected workspaces.
   */
  getTotalLocalChangeCount(): number {
    let count = 0;
    for (const ws of this.getConnectedWorkspaces()) {
      const changes = getWorkspaceChanges(ws.workspaceUri);
      if (changes) {
        count += changes.localChanges.length;
      }
    }
    return count;
  }

  /**
   * Get total remote change count across all connected workspaces.
   */
  getTotalRemoteChangeCount(): number {
    let count = 0;
    for (const ws of this.getConnectedWorkspaces()) {
      const changes = getWorkspaceChanges(ws.workspaceUri);
      if (changes) {
        count += changes.remoteChanges.length;
      }
    }
    return count;
  }
}

let treeView: TreeView<AgentChangesTreeItemUnion> | undefined;
let treeDataProvider: AgentChangesTreeDataProvider | undefined;

/**
 * Initialize the Agent Changes tree view.
 * Call this from extension.ts after LSP client is ready.
 */
export function initializeAgentChangesTree(context: ExtensionContext): void {
  treeDataProvider = new AgentChangesTreeDataProvider();
  treeView = window.createTreeView('agent-changes', {
    treeDataProvider,
    showCollapseAll: false,
  });

  context.subscriptions.push(treeView);

  // Subscribe to workspace changes to refresh the tree (with proper disposal)
  const workspaceSubscription = addWorkspaceChangeSubscription(() => {
    treeDataProvider?.refresh();
    void treeDataProvider?.probeAccountConnectivity();
  });
  context.subscriptions.push(workspaceSubscription);

  // Update badge when tree data changes (with proper disposal)
  const treeChangeSubscription = treeDataProvider.onDidChangeTreeData(() => {
    updateViewBadge();
    updateContextKeys();
  });
  context.subscriptions.push(treeChangeSubscription);

  // Update the `mcs.isSyncing` context key and ensure the active agent is expanded so its loading state is visible.
  const syncStateSubscription = onAnySyncStateChanged(() => {
    treeDataProvider?.refresh();
    updateSyncInProgressContextKey();
    revealActiveAgent();
  });
  context.subscriptions.push(syncStateSubscription);

  const authStateSubscription = onAuthStateChanged(() => {
    treeDataProvider?.refresh();
    void treeDataProvider?.probeAccountConnectivity();
  });
  context.subscriptions.push(authStateSubscription);

  // Initial badge update
  updateViewBadge();
  updateContextKeys();
  updateSyncInProgressContextKey();
  void treeDataProvider.probeAccountConnectivity();
}

/**
 * Update the view badge to show local change count.
 * Exported so it can be called after initial changes load.
 */
export function updateViewBadge(): void {
  if (!treeView || !treeDataProvider) {
    return;
  }

  // WAS: Don't show badge when Agent Changes view is disabled (SCM mode) 
  // --- SWITCH IS DEPRECATED, but we are deferring removal for now
  const useAgentChangesView = true;// workspace.getConfiguration('ms-CopilotStudio').get<boolean>('useAgentChangesView', true);
  if (!useAgentChangesView) {
    treeView.badge = undefined;
    return;
  }

  const localCount = treeDataProvider.getTotalLocalChangeCount();
  
  if (localCount > 0) {
    treeView.badge = {
      value: localCount,
      tooltip: `${localCount} local change${localCount === 1 ? '' : 's'}`
    };
  } else {
    treeView.badge = undefined;
  }
}

/**
 * Update context keys for Apply button enablement and welcome content.
 */
function updateContextKeys(): void {
  if (!treeDataProvider) {
    return;
  }

  const hasRemoteChanges = treeDataProvider.getTotalRemoteChangeCount() > 0;
  const hasLocalChanges = treeDataProvider.getTotalLocalChangeCount() > 0;
  const hasChanges = hasRemoteChanges || hasLocalChanges;

  void commands.executeCommand('setContext', 'mcs.agentChangesView.hasRemoteChanges', hasRemoteChanges);
  void commands.executeCommand('setContext', 'mcs.agentChangesView.hasLocalChanges', hasLocalChanges);
  void commands.executeCommand('setContext', 'mcs.agentChangesView.hasChanges', hasChanges);
}

/**
 * Set the `mcs.isSyncing` context key so command enablement clauses can
 * disable sync buttons (Preview / Get / Apply) while any sync is running.
 */
function updateSyncInProgressContextKey(): void {
  void commands.executeCommand('setContext', 'mcs.isSyncing', getActiveSyncUri() !== undefined);
}

/** Reveal and expand the active sync agent so its loading state is visible. */
function revealActiveAgent(): void {
  const activeUri = getActiveSyncUri();
  if (!activeUri || !treeView || !treeDataProvider) {
    return;
  }
  const activeAgent = treeDataProvider.getChildren()
    .find((c): c is AgentTreeItem => c.kind === AgentChangesItemKind.Agent && c.workspace.workspaceUri === activeUri);
  if (activeAgent) {
    void treeView.reveal(activeAgent, { expand: true, focus: false, select: false });
  }
}

/**
 * Refresh the Agent Changes tree view.
 * Call this after sync operations complete.
 */
export function refreshAgentChangesTree(): void {
  treeDataProvider?.refresh();
  // Also update badge and context keys directly to ensure they're current
  // This handles cases where the tree view might not be visible yet
  updateViewBadge();
  updateContextKeys();
  updateSyncInProgressContextKey();
}
