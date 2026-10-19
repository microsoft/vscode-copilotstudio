import { CancellationToken, Uri } from 'vscode';
import { AccountInfo, EnvironmentInfo } from '../types';
import { LspMethods, TelemetryEventType } from '../constants';
import { buildLspRequestPayload, lspClient } from '../services/lspClient';
import logger, { PiiRedactionType, sanitizeErrorDetails } from '../services/logger';

const WORKFLOW_DEFINITION_FILE_NAME = 'workflow.json';

export interface StandaloneWorkflowView {
  workflowId: string;
  name: string;
  description?: string;
  isActivated: boolean;
  modifiedOn?: string;
  connectionReferences: string[];
}

interface ResponseBase {
  code: number;
  message: string;
}

interface ListStandaloneWorkflowsResponse extends ResponseBase {
  workflows?: StandaloneWorkflowView[];
}

export interface CloneStandaloneWorkflowResponse extends ResponseBase {
  workflow?: StandaloneWorkflowView;
  projectFolder?: string;
  folderName?: string;
  changed: boolean;
}

export interface CreateStandaloneWorkflowResponse extends ResponseBase {
  workflowId: string;
  projectFolder?: string;
  folderName?: string;
  writtenFiles: string[];
}

interface DeleteStandaloneWorkflowResponse extends ResponseBase {
  deleted: boolean;
}

export class StandaloneWorkflowRequestError extends Error {
  public constructor(public readonly code: number, message: string) {
    super(message);
    this.name = 'StandaloneWorkflowRequestError';
  }
}

const succeed = <TResponse extends ResponseBase>(response: TResponse | undefined): TResponse => {
  if (!response) {
    throw new StandaloneWorkflowRequestError(500, 'The language server returned no response.');
  }

  if (response.code !== 200) {
    throw new StandaloneWorkflowRequestError(response.code, response.message || 'The request failed.');
  }

  return response;
};

/**
 * Reports a standalone workflow failure once, showing the reason to the user while redacting
 * the workflow name and environment identifier the server echoed back from telemetry.
 */
export const reportStandaloneWorkflowFailure = (
  event: TelemetryEventType,
  summary: string,
  error: unknown,
  fallbackReason: string,
  workflowName?: string,
  environmentId?: string): void => {
  const reason = error instanceof Error && error.message ? error.message : fallbackReason;

  logger.logError(event, summary, {
    error: sanitizeErrorDetails(
      reason,
      workflowName ? [workflowName] : [],
      PiiRedactionType.WorkflowNames,
      environmentId ? [{ values: [environmentId], type: PiiRedactionType.EnvironmentId }] : []),
  });
};

/**
 * Resolves the workspace to open and the definition file to reveal after a create or clone.
 * The chosen container is opened, as agent cloning does, so a workspace already open survives
 * and the new project is discovered beneath it.
 */
export const resolveStandaloneWorkflowProject = (
  root: Uri,
  response: { projectFolder?: string; folderName?: string }): { workspaceRoot: Uri; entryFile?: Uri } => ({
  workspaceRoot: root,
  entryFile: response.folderName ? Uri.joinPath(root, ...response.folderName.split('/'), WORKFLOW_DEFINITION_FILE_NAME) : undefined,
});

/**
 * Lists the cloud flows an environment holds.
 */
export const listStandaloneWorkflows = async (
  environmentInfo: EnvironmentInfo,
  account: Partial<AccountInfo>,
  nameFilter?: string,
  maximumCount?: number,
  cancellationToken?: CancellationToken): Promise<StandaloneWorkflowView[]> => {
  const response = await lspClient.sendRequest<ListStandaloneWorkflowsResponse>(
    LspMethods.LIST_STANDALONE_WORKFLOWS,
    { ...await buildLspRequestPayload(undefined, environmentInfo, account, true), nameFilter, maximumCount },
    cancellationToken);

  return succeed(response).workflows ?? [];
};

/**
 * Downloads one cloud flow into a folder as a standalone workflow workspace.
 */
export const cloneStandaloneWorkflow = async (
  environmentInfo: EnvironmentInfo,
  account: Partial<AccountInfo>,
  workflowId: string,
  rootFolder: string,
  cancellationToken?: CancellationToken): Promise<CloneStandaloneWorkflowResponse> => {
  const response = await lspClient.sendRequest<CloneStandaloneWorkflowResponse>(
    LspMethods.CLONE_STANDALONE_WORKFLOW,
    { ...await buildLspRequestPayload(undefined, environmentInfo, account, true), workflowId, rootFolder },
    cancellationToken);

  return succeed(response);
};

/**
 * Scaffolds a new standalone workflow in a folder, without touching the cloud.
 */
export const createStandaloneWorkflow = async (
  name: string,
  rootFolder: string,
  cancellationToken?: CancellationToken): Promise<CreateStandaloneWorkflowResponse> => {
  const response = await lspClient.sendRequest<CreateStandaloneWorkflowResponse>(
    LspMethods.CREATE_STANDALONE_WORKFLOW,
    { name, rootFolder },
    cancellationToken);

  return succeed(response);
};

/**
 * Deletes one cloud flow from an environment, leaving local files untouched.
 */
export const deleteStandaloneWorkflow = async (
  environmentInfo: EnvironmentInfo,
  account: Partial<AccountInfo>,
  workflowId: string,
  cancellationToken?: CancellationToken): Promise<boolean> => {
  const response = await lspClient.sendRequest<DeleteStandaloneWorkflowResponse>(
    LspMethods.DELETE_STANDALONE_WORKFLOW,
    { ...await buildLspRequestPayload(undefined, environmentInfo, account, true), workflowId },
    cancellationToken);

  return succeed(response).deleted;
};
