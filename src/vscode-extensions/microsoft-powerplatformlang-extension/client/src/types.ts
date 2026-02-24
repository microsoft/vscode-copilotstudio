export interface RemoteApiRequest {
  environmentInfo: EnvironmentInfo;
  dataverseAccessToken: string;
  copilotStudioAccessToken: string;
  accountInfo: AccountInfo;
  solutionVersions: SolutionInfo
}

export interface RemoteApiResponse {
  code: number;
  message?: string;
};

export interface SolutionInfo {
  solutionVersions: Record<string, string>;
  copilotStudioSolutionVersion: string;
}

export interface AgentSyncInfo {
  dataverseEndpoint: string;
  agentManagementEndpoint: string;
  environmentId: string;

  // TODO: This will break with component collections.
  // This will break with component collections.
  agentId: string;

  accountInfo: AccountInfo;
  solutionVersions: SolutionInfo;
}

export interface SyncRequest extends RemoteApiRequest {
  // Workspace URI: get from vscode.WorkspaceFolder;
  workspaceUri: string;
}

export interface SyncResponse extends RemoteApiResponse {
  localChanges: Change[];
}

export interface GetFileRequest {
  // Workspace URI: get from vscode.WorkspaceFolder;
  workspaceUri: string;
  schemaName: string;
}

export interface GetFileResponse extends RemoteApiResponse {
  content: string;
}

export interface RemoteFileRequest extends RemoteApiRequest, GetFileRequest {}

export interface DiffRequest {
  // Workspace URI: get from vscode.WorkspaceFolder;
  workspaceUri: string;
}

export interface Change {
  name: string;
  schemaName: string;
  uri: string;
  changeType: ChangeType;
  changeKind: string;
}

export enum ChangeType {
  Create = 0,
  Update = 1,
  Delete = 2,
}

export interface CloneAgentRequest extends RemoteApiRequest {
  agentInfo: AgentInfo;
  assets: ClonedAssets;
  rootFolder: string; // The full path to the folder that should contain the agent
}

export interface CloneAgentResponse extends RemoteApiResponse {
  agentFolderName?: string; // canonical sanitized top-level agent folder name
}

export interface AgentInfo {
  agentId: string; // Guid in C# maps to string in TypeScript
  displayName: string;
  iconBase64: string;
  displayComplement: string;
  componentCollections: ComponentCollection[];
}

export interface ComponentCollection {
  id: string;
  schemaName: string;
  displayName: string;
}

export interface EnvironmentInfo {
  environmentId: string;
  dataverseUrl: string;
  displayName: string;
  agentManagementUrl: string;
  environmentSku?: string;  // Developer, Default, Sandbox, Production, etc.
}

export interface AgentIdentifier {
  clusterCategory: number; // Maps to CoreServicesClusterCategory
  environmentId: string;
  agentId?: string; // Nullable Guid in C# maps to optional string in TypeScript
}

export interface IdentifyAgentResponse {
  agentIdentifier?: AgentIdentifier;
  environmentInfo?: EnvironmentInfo;
  agentInfo?: AgentInfo;
  accountName?: string;
  accountId?: string;
}

export interface AccountInfo {
  clusterCategory?: number;
  tenantId: string;
  accountId?: string;
  accountEmail?: string;
}

export interface ClonedAssets {
  componentcollectionIds: string[];
  cloneAgent: boolean
}

export interface ReattachAgentRequest extends RemoteApiRequest {
  workspaceUri: string;
}

export interface ReattachAgentResponse extends RemoteApiResponse {
  agentSyncInfo: AgentSyncInfo;
  isNewAgent: boolean;
}
