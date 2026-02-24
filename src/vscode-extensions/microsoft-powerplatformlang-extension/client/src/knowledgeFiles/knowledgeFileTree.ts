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
  const files: [string, vscode.FileType][] = await this.provider.readDirectory(vscode.Uri.parse('virtualKnowledge:/'));
  return files.map(([name, type]: [string, vscode.FileType]) => {
    const uri = vscode.Uri.parse(`virtualKnowledge:/${name}`);
    const collapsibleState = type === vscode.FileType.Directory ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None;
    return new knowledgeTreeItem(name, uri, collapsibleState);
  });
}


  getTreeItem(element: knowledgeTreeItem): vscode.TreeItem {
    return element;
  }
}
