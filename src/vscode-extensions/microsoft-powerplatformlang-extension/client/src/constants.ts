export const LspMethods = {
  AGENT_DIRECTORY_CHANGE: "powerplatformls/onAgentDirectoryChange",
  CLONE_AGENT: "powerplatformls/cloneAgent",
  GET_AGENT: "powerplatformls/getAgent",
  GET_CACHED_FILE: "powerplatformls/getCachedFile",
  GET_ENVIRONMENT: "powerplatformls/getEnvironment",
  GET_LOCAL_CHANGES: "powerplatformls/getLocalChanges",
  GET_REMOTE_CHANGES: "powerplatformls/getRemoteChanges",
  GET_REMOTE_FILE: "powerplatformls/getRemoteFile",
  GET_WORKSPACE_DETAILS: "powerplatformls/getWorkspaceDetails",
  IDENTIFY_AGENT: "powerplatformls/identifyAgent",
  LIST_AGENTS: "powerplatformls/listAgents",
  LIST_ENVIRONMENTS: "powerplatformls/listEnvironments",
  LIST_WORKSPACES: "workspace/listWorkspaces",
  REATTACH_AGENT: "powerplatformls/reattachAgent",
  SYNC_PULL: "powerplatformls/syncPull",
  SYNC_PUSH: "powerplatformls/syncPush",
} as const;

export enum CoreServicesClusterCategory {
  Exp = 0,
  Dev = 1,
  Test = 2,
  Preprod = 3,
  FirstRelease = 4,
  Prod = 5,
  Gov = 6,
  High = 7,
  DoD = 8,
  Mooncake = 9,
  Ex = 10,
  Rx = 11,
  Prv = 12,
  Local = 13,
  GovFR = 14
}

export const DefaultCoreServicesClusterCategory = CoreServicesClusterCategory.Prod;

export const DEFAULT_DOTNET_VERSION = "8.0";

export const TELEMETRY_CONNECTION_STRING = "InstrumentationKey=97d065e0-07ee-4d5c-952d-0e03ae8fc0d3;IngestionEndpoint=https://westus-0.in.applicationinsights.azure.com/;LiveEndpoint=https://westus.livediagnostics.monitor.azure.com/;ApplicationId=d701ad1c-902d-4151-b350-3194b11daa36";

export const TelemetryEventsKeys = {
  CopilotStudioStart: "CopilotStudioStart",
  LanguageServerInfo: "LanguageServerInfo",
  LanguageServerError: "LanguageServerError",
  CloneAgentClick: "CloneAgentClick",
  CloneAgentCancel: "CloneAgentCancel",
  CloneAgentSuccess: "CloneAgentSuccess",
  CloneAgentError: "CloneAgentError",
  DotnetVersionWarning: "DotnetVersionWarning",
  UnixPlatformError: "UnixPlatformError",
  ResetAccountError: "ResetAccountError",
  SwitchAccountError: "SwitchAccountError",
  SignInError: "SignInError",
  DeleteBotComponentError: "DeleteBotComponentError",
  GetBotPrefixError: "GetBotPrefixError",
  DownloadKnowledgeFileError: "DownloadKnowledgeFileError",
  VirtualKnowledgeFileProgress: "VirtualKnowledgeFileProgress",
  VirtualKnowledgeFileError: "VirtualKnowledgeFileError",
  UploadKnowledgeFileSuccess: "UploadKnowledgeFileSuccess",
  UploadKnowledgeFileWarning: "UploadKnowledgeFileWarning",
  UploadKnowledgeFileError: "UploadKnowledgeFileError",
  OpenKnowledgeFileError: "OpenKnowledgeFileError",
  SaveKnowledgeFileError: "SaveKnowledgeFileError",
  GetAccessTokenError: "GetAccessTokenError",
  SyncWorkspaceClick: "SyncWorkspaceClick",
  SyncWorkspaceCancel: "SyncWorkspaceCancel",
  SyncWorkspaceSuccess: "SyncWorkspaceSuccess",
  SyncWorkspaceError: "SyncWorkspaceError",
  GetRemoteFileError: "GetRemoteFileError",
  GetLocalFileError: "GetLocalFileError",
  ReadKnowledgeFileError: "ReadKnowledgeFileError",
  ReattachAgentError: "ReattachAgentError",
  ReattachAgentInfo: "ReattachAgentInfo",
  PostOpenInstruction: "PostOpenInstruction",
  PostOpenError: "PostOpenError",
  WorkspaceRefreshLoopCap: "WorkspaceRefreshLoopCap",
  LoadEnvironmentError: "LoadEnvironmentError",
  LoadEnvironmentSuccess: "LoadEnvironmentSuccess",
} as const;

export type TelemetryEventType = typeof TelemetryEventsKeys[keyof typeof TelemetryEventsKeys];

export enum LogLevel {
  Info = 'Info',
  Warning = 'Warning',
  Error = 'Error'
}

export const ConflictResolution = {
  UseRemote: 'Use Remote',
  UseLocal: 'Use Local',
  Merge: 'Merge',
} as const;
