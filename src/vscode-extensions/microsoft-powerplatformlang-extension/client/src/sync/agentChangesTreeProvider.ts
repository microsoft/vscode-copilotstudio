import { commands, EventEmitter, ExtensionContext, ThemeColor, ThemeIcon, TreeDataProvider, TreeItem, TreeItemCollapsibleState, TreeView, window, workspace } from "vscode";
import { addWorkspaceChangeSubscription, CopilotStudioWorkspace, getAllWorkspaces, hasConnectionFileInWorkspace } from "./localWorkspaces";
import { Resource } from "./changeTracking";
import { getWorkspaceChanges } from "./workspaceScm";
import { ChangeType } from "../types";

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

  getTreeItem(element: AgentChangesTreeItemUnion): TreeItem {
    switch (element.kind) {
      case AgentChangesItemKind.Agent: {
        const item = new TreeItem(element.workspace.displayName, TreeItemCollapsibleState.Expanded);
        item.iconPath = element.workspace.icon;
        item.description = element.workspace.description;
        item.contextValue = 'agent';
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
        item.iconPath = new ThemeIcon(element.groupType === 'local' ? 'file-code' : 'cloud');
        item.contextValue = `changeGroup-${element.groupType}`;
        return item;
      }
      case AgentChangesItemKind.ChangeItem: {
        const resource = element.resource;
        const fileName = resource.resourceUri.path.split('/').pop() || resource.resourceUri.path;
        const item = new TreeItem(fileName, TreeItemCollapsibleState.None);
        
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

  getChildren(element?: AgentChangesTreeItemUnion): AgentChangesTreeItemUnion[] {
    if (!element) {
      // Root level: return all connected agents
      return this.getConnectedWorkspaces().map(ws => ({
        kind: AgentChangesItemKind.Agent,
        workspace: ws,
      }));
    }

    switch (element.kind) {
      case AgentChangesItemKind.Agent: {
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
    return getAllWorkspaces().filter(ws =>
      ws.syncInfo &&
      ws.syncInfo.agentManagementEndpoint &&
      hasConnectionFileInWorkspace(ws.workspaceUri)
    );
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
  });
  context.subscriptions.push(workspaceSubscription);

  // Update badge when tree data changes (with proper disposal)
  const treeChangeSubscription = treeDataProvider.onDidChangeTreeData(() => {
    updateViewBadge();
    updateContextKeys();
  });
  context.subscriptions.push(treeChangeSubscription);

  // Initial badge update
  updateViewBadge();
  updateContextKeys();
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
 * Refresh the Agent Changes tree view.
 * Call this after sync operations complete.
 */
export function refreshAgentChangesTree(): void {
  treeDataProvider?.refresh();
  // Also update badge and context keys directly to ensure they're current
  // This handles cases where the tree view might not be visible yet
  updateViewBadge();
  updateContextKeys();
}
