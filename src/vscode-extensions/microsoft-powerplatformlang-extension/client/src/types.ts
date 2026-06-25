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
  environmentDisplayName?: string;

  // TODO: This will break with component collections.
  agentId: string;

  accountInfo: AccountInfo;
  solutionVersions: SolutionInfo;
}

export interface SyncRequest extends RemoteApiRequest {
  // Workspace URI: get from vscode.WorkspaceFolder;
  workspaceUri: string;
  draftConnectionReferenceWorkflows?: boolean;
}

export interface SyncResponse extends RemoteApiResponse {
  localChanges: Change[];
  workflowResponse: WorkflowResponse[];
  aiPromptResponse?: AIPromptResponse[];
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

export interface KnowledgeFileInfo {
  schemaName: string;
  fileName: string;
  relativePath: string;
}

export interface ListKnowledgeFilesRequest extends RemoteApiRequest {
  workspaceUri: string;
}

export interface ListKnowledgeFilesResponse extends RemoteApiResponse {
  files: KnowledgeFileInfo[];
}

export interface DownloadKnowledgeFilesRequest extends RemoteApiRequest {
  workspaceUri: string;
  schemaNames?: string[];
}

export interface DownloadKnowledgeFilesResponse extends RemoteApiResponse {
  downloaded: KnowledgeFileInfo[];
}

export interface UploadKnowledgeFilesRequest extends RemoteApiRequest {
  workspaceUri: string;
}

export interface UploadKnowledgeFilesResponse extends RemoteApiResponse {
  uploaded: string[];
}

export interface DiffRequest extends RemoteApiRequest {
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
  schemaName: string;
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
  accountEmail?: string;
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
  allowRetarget?: boolean;
  conflictResolution?: RetargetConflictResolution;
}

export enum RetargetConflictResolution {
  Prompt = 0,
  ReuseExisting = 1,
}

export interface ReattachAgentResponse extends RemoteApiResponse {
  agentSyncInfo: AgentSyncInfo;
  isNewAgent: boolean;
  requiresLocalPush?: boolean;
  schemaConflict?: boolean;
  workflowResponse: WorkflowResponse[];
  aiPromptResponse?: AIPromptResponse[];
}

export interface ConnectionNeeded {
  connectionReferenceLogicalName: string;
  connectorId: string;
  connectorName: string;
  boundConnectionId: string;
}

export interface ConnectionInstance {
  name: string;
  displayName: string;
  status: string;
  owner: string;
}

export enum UsageKind {
  Action = 0,
  Topic = 1,
  Workflow = 2,
  Connector = 3,
  ConnectionReferencesFile = 4,
  BotDefinition = 5
}

export interface ConnectionReferenceUsage {
  logicalName: string;
  filePath: string;
  kind: UsageKind;
  displayName: string;
}

export enum WorkflowState {
  Unknown = 0,
  Draft = 1,
  Activated = 2,
  Suspended = 3
}

export interface AgentConnectionView {
  connectionReferenceLogicalName: string;
  connectorId: string;
  connectorName: string;
  boundConnectionId: string;
  boundConnectionExists: boolean;
  candidates: ConnectionInstance[];
  usages: ConnectionReferenceUsage[];
  isDeclared: boolean;
  catalogUnavailable?: boolean;
}

export interface WorkflowStatusView {
  workflowId: string;
  displayName: string;
  filePath: string;
  state: WorkflowState;
  connectionReferenceLogicalNames: string[];
  canEnable: boolean;
}

export interface ConnectionBindingRequest {
  connectionReferenceLogicalName: string;
  connectionId: string;
  connectionDisplayName?: string;
}

export interface ListAgentConnectionsRequest extends RemoteApiRequest {
  workspaceUri: string;
  connectionsAccessToken?: string;
}

export interface ListAgentConnectionsResponse extends RemoteApiResponse {
  agentConnections?: AgentConnectionView[];
}

export interface ApplyConnectionBindingsRequest extends RemoteApiRequest {
  workspaceUri: string;
  connectionsAccessToken?: string;
  bindings: ConnectionBindingRequest[];
}

export interface ApplyConnectionBindingsResponse extends RemoteApiResponse {
  agentConnections?: AgentConnectionView[];
}

export interface ListWorkflowStatusRequest extends RemoteApiRequest {
  workspaceUri: string;
  connectionsAccessToken?: string;
}

export interface ListWorkflowStatusResponse extends RemoteApiResponse {
  workflows?: WorkflowStatusView[];
}

export interface WorkflowStateChange {
  workflowId: string;
  activate: boolean;
}

export interface SetWorkflowStatesRequest extends RemoteApiRequest {
  workspaceUri: string;
  changes: WorkflowStateChange[];
  connectionsAccessToken?: string;
}

export interface SetWorkflowStatesResponse extends RemoteApiResponse {
  succeeded: boolean;
  workflows?: WorkflowStatusView[];
}

export interface DeclareConnectionReferencesRequest extends RemoteApiRequest {
  workspaceUri: string;
  logicalNames: string[];
  connectionsAccessToken?: string;
}

export interface DeclareConnectionReferencesResponse extends RemoteApiResponse {
  agentConnections?: AgentConnectionView[];
  invalidLogicalNames?: string[];
}

export interface RemoveConnectionReferenceRequest extends RemoteApiRequest {
  workspaceUri: string;
  logicalName: string;
  confirmed: boolean;
}

export interface RemoveConnectionReferenceResponse extends RemoteApiResponse {
  removed: boolean;
  usages?: ConnectionReferenceUsage[];
}

export interface ConnectorInfo {
  internalId: string;
  displayName: string;
  publisher: string;
  tier: string;
  iconUri: string;
}

export interface ListConnectorsRequest extends RemoteApiRequest {
  workspaceUri: string;
  connectionsAccessToken?: string;
}

export interface ListConnectorsResponse extends RemoteApiResponse {
  connectors?: ConnectorInfo[];
}

export interface CreateConnectionReferenceRequest extends RemoteApiRequest {
  workspaceUri: string;
  connectorInternalId: string;
  connectionsAccessToken?: string;
}

export interface CreateConnectionReferenceResponse extends RemoteApiResponse {
  logicalName: string;
  agentConnections?: AgentConnectionView[];
}

export interface WorkflowResponse {
  workflowName: string;
  isDisabled: boolean;
  errorMessage?: string;
}

export interface AIPromptResponse {
  promptName: string;
  errorMessage?: string;
}