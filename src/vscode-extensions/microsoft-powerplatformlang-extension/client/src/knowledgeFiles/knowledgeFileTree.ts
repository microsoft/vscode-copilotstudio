import * as vscode from 'vscode';
import { virtualKnowledgeFileSystemProvider } from './virtualKnowledgeFile';

export class knowledgeTreeItem extends vscode.TreeItem {
  constructor(
    public readonly label: string,
    public readonly uri: vscode.Uri,
    public readonly collapsibleState: vscode.TreeItemCollapsibleState = vscode.TreeItemCollapsibleState.None
  ) {
    super(label, collapsibleState);
    this.resourceUri = uri;
    this.command = {
      command: 'virtualKnowledge.openLocal',
      title: 'Download and Open Local File',
      arguments: [uri]
    };
    this.contextValue = 'knowledgeFile';
  }
}

export class knowledgeTreeDataProvider implements vscode.TreeDataProvider<knowledgeTreeItem> {
  private _onDidChangeTreeData: vscode.EventEmitter<knowledgeTreeItem | undefined | void> = new vscode.EventEmitter<knowledgeTreeItem | undefined | void>();
  readonly onDidChangeTreeData: vscode.Event<knowledgeTreeItem | undefined | void> = this._onDidChangeTreeData.event;

  constructor(private provider: virtualKnowledgeFileSystemProvider) {}

  refresh(): void {
    this._onDidChangeTreeData.fire();
  }

  async getChildren(): Promise<knowledgeTreeItem[]> {
    const entries = this.provider.getEntries();
    return entries.map(({ uri, label }) => new knowledgeTreeItem(label, uri, vscode.TreeItemCollapsibleState.None));
  }


  getTreeItem(element: knowledgeTreeItem): vscode.TreeItem {
    return element;
  }
}
