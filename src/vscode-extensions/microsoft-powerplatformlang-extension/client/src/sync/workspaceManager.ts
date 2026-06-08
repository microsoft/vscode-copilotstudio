import { EventEmitter, ExtensionContext, TreeDataProvider, TreeItem, TreeItemCollapsibleState, window } from "vscode";
import { addWorkspaceChangeSubscription, CopilotStudioWorkspace, getAllWorkspaces, initializeLocalWorkspaces } from "./localWorkspaces";
import { AgentFormat } from "../types";

export function initializeWorkspaceManager(context: ExtensionContext) {
  initializeLocalWorkspaces(context);
  const treeDataProvider = new AgentTreeDataProvider();
  const treeView = window.createTreeView('workspace-agents', {
    treeDataProvider,
    showCollapseAll: false,
  });
  treeView.description = "Local";
  context.subscriptions.push(treeView);
}

class AgentTreeDataProvider implements TreeDataProvider<CopilotStudioWorkspace> {
  private _onDidChangeTreeData: EventEmitter<CopilotStudioWorkspace | undefined | void> = new EventEmitter<CopilotStudioWorkspace | undefined | void>();
  readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

  constructor() {
    addWorkspaceChangeSubscription(() => {
      this._onDidChangeTreeData.fire();
    });
  }

  getTreeItem(element: CopilotStudioWorkspace): TreeItem {
    const item = new TreeItem(element.displayName, TreeItemCollapsibleState.None);
    item.iconPath = element.icon;
    const formatBadge = formatBadgeFor(element.syncInfo?.format);
    item.description = formatBadge ? `${formatBadge}  ${element.description}` : element.description;
    item.label = element.displayName;
    return item;
  }

  getChildren(element?: CopilotStudioWorkspace): Thenable<CopilotStudioWorkspace[]> {
    if (!element) {
      return Promise.resolve(getAllWorkspaces());
    } else {
      return Promise.resolve([]);
    }
  }
}

function formatBadgeFor(format: AgentFormat | undefined): string | undefined {
  switch (format) {
    case AgentFormat.Cli: return 'CLI';
    case AgentFormat.Classic: return 'Classic';
    default: return undefined;
  }
}