import { EventEmitter, ExtensionContext, TreeDataProvider, TreeItem, TreeItemCollapsibleState, Uri, window } from "vscode";
import { addWorkspaceChangeSubscription, CopilotStudioWorkspace, getAllWorkspaces, initializeLocalWorkspaces } from "./localWorkspaces";
import { isCliLayeredWorkspace } from "../knowledgeFiles/syncUtils";

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
    const formatBadge = formatBadgeFor(element);
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

function formatBadgeFor(element: CopilotStudioWorkspace): string | undefined {
  if (!element.workspaceUri) {
    return undefined;
  }
  return isCliLayeredWorkspace(Uri.parse(element.workspaceUri).fsPath) ? 'CLI' : 'Classic';
}