import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs/promises';
import { getAccessTokenByAccountId } from '../clients/account';
import { botComponentHandler } from '../botComponents/botComponentHandler';
import logger from '../services/logger';
import { AgentSyncInfo, Change, ChangeType } from '../types';
import { ChangeTrack } from './fileHelper';
import { TelemetryEventsKeys } from '../constants';

export function getFilesDir(workspaceUri: string, agentSchemaName?: string): string {
  const root = vscode.Uri.parse(workspaceUri).fsPath;

  if (agentSchemaName && agentSchemaName.includes('.agent.')) {
    const agentFolder = agentSchemaName.split('.agent.').pop();
    return path.join(root, 'agents', agentFolder!, 'knowledge', 'files');
  }

  return path.join(root, 'knowledge', 'files');
}

export function getTrackPath(workspaceUri: string): string {
  const root = vscode.Uri.parse(workspaceUri).fsPath;
  return path.join(root, '.mcs', 'filechangetrack.json');
}

export async function getDataverseBotHandler(syncInfo: AgentSyncInfo): Promise<botComponentHandler> {
  try {
    const endpoint = vscode.Uri.parse(syncInfo.dataverseEndpoint);
    const token = await getAccessTokenByAccountId(endpoint, syncInfo.accountInfo.accountId);
    return new botComponentHandler(syncInfo.dataverseEndpoint, token.accessToken);
  } catch (err) {
    logger.logError(TelemetryEventsKeys.GetAccessTokenError, `Failed to get access token: <pii>${(err as Error).message}</pii>`);
    throw err;
  }
}

export async function safeSaveFile(fullPath: string, tempPath: string, content: Buffer): Promise<void> {
  await fs.writeFile(tempPath, content);

  try {
    await fs.mkdir(path.dirname(fullPath), { recursive: true });
    await fs.rename(tempPath, fullPath);
  } catch (err: any) {
    if (err.code === 'EXDEV' || err.code === 'EPERM' || err.code === 'EACCES') {
      // If rename fails due to cross-device (like in linux arm64), copy instead
      try {
        await fs.copyFile(tempPath, fullPath);
        await fs.unlink(tempPath).catch(() => { /* best-effort cleanup */ });
      } catch (copyErr) {
        logger.logError(TelemetryEventsKeys.SaveKnowledgeFileError, `Failed to copy <pii>${fullPath}</pii>: ${copyErr}`);
        throw copyErr;
      }
    } else {
      logger.logError(TelemetryEventsKeys.SaveKnowledgeFileError, `Failed to move <pii>${fullPath}</pii>: ${err}`);
      throw err;
    }
  }
}

const readTrackingFile = async (path: string): Promise<ChangeTrack | null> => {
  try {
    const content = await fs.readFile(path, 'utf-8');
    return JSON.parse(content);
  } catch (error) {
    // file name instead of full path to avoid logging PII. Using :: instead of / so it is not flagged as PII in telemetry.
    const trackingFile = JSON.stringify(path.split(/[\\/]/).slice(-2).join('::'));
    logger.logWarning(TelemetryEventsKeys.ReadKnowledgeFileError, undefined, {
      message: `Could not read tracking file ${trackingFile}: ${(error as Error).message}`
    });
    return null;
  }
};

const readKnowledgeFilesDir = async (path: string): Promise<string[]> => {
  try {
    const files = await fs.readdir(path);
    return files.filter(file => !file.endsWith('.mcs.yml'));
  } catch (error) {
    // directory name instead of full path to avoid logging PII. Using :: instead of / so it is not flagged as PII in telemetry.
    const knowledgeFilesDir = JSON.stringify(path.split(/[\\/]/).slice(-2).join('::'));
    logger.logWarning(TelemetryEventsKeys.ReadKnowledgeFileError, undefined, {
      message: `Could not read local directory ${knowledgeFilesDir}: ${(error as Error).message}`
    });
    return [];
  }
};

export async function getKnowledgeLocalChanges(syncInfo: AgentSyncInfo, workspaceUri: string): Promise<Change[]> {
  const trackingData = await readTrackingFile(getTrackPath(workspaceUri));
  const localFiles = await getAllKnowledgeFiles(workspaceUri);

  if (!trackingData || !localFiles.length) {
    return [];
  }

  const changes: Change[] = [];
  const botHandler = await getDataverseBotHandler(syncInfo);
  const wsMeta = await botHandler.listWsComponentMetadata(syncInfo);
  const remoteFiles = new Set(wsMeta.map(metadata => decodeURIComponent(metadata.filename ?? '')));

  for (const file of localFiles) {

    const { localChangeType, schema } = trackingData[file.name] || {};
    const isRemoteFile = remoteFiles.has(file.name);

    if (!isRemoteFile || localChangeType !== undefined) {

      changes.push({
        name: file.name,
        uri: vscode.Uri.file(file.fullPath).toString(),
        schemaName: schema ?? '',
        changeKind: 'knowledge',
        changeType: !isRemoteFile ? ChangeType.Create : localChangeType!
      });
    }
  }

  return changes;
}

export async function getKnowledgeRemoteChanges(syncInfo: AgentSyncInfo, workspaceUri: string): Promise<Change[]> {
  const trackingData = await readTrackingFile(getTrackPath(workspaceUri)) || {};
  const localFiles = await getAllKnowledgeFiles(workspaceUri);
  const localFileMap = new Map(localFiles.map(f => [f.name.toLowerCase(), f]));
  const changes: Change[] = [];
  const botHandler = await getDataverseBotHandler(syncInfo);
  const wsMeta = await botHandler.listWsComponentMetadata(syncInfo);
  const remoteFiles = new Map<string, { fileName: string, schemaName: string }>();
  for (const { filename, schemaName } of wsMeta) {
    const decodedFile = decodeURIComponent(filename ?? '');
    if (decodedFile) {
      remoteFiles.set(decodedFile.toLowerCase(), {
        fileName: decodedFile,
        schemaName: schemaName ?? '',
      });
    }
  }

  for (const { fileName, schemaName } of remoteFiles.values()) {
    if (!localFileMap.has(fileName.toLowerCase())) {
      const fullPath = path.join(vscode.Uri.parse(workspaceUri).fsPath, 'knowledge', 'files', fileName);
      const fileUri = vscode.Uri.file(fullPath).toString();
      changes.push({
        uri: fileUri,
        name: fileName,
        schemaName,
        changeKind: 'knowledge',
        changeType: ChangeType.Create
      });
    }
  }

  for (const fileName in trackingData) {
    const { remoteChangeType, schema } = trackingData[fileName];
    if (remoteChangeType !== undefined) {
      const file = localFileMap.get(fileName.toLowerCase());
      const fullPath = file ? file.fullPath : path.join(vscode.Uri.parse(workspaceUri).fsPath, 'knowledge', 'files', fileName);
      const fileUri = vscode.Uri.file(fullPath).toString();
      changes.push({
        uri: fileUri,
        name: fileName,
        schemaName: schema ?? '',
        changeKind: 'knowledge',
        changeType: remoteChangeType
      });
    }
  }

  return changes;
}

export async function getAllKnowledgeFiles(workspaceUri: string): Promise<{ name: string; fullPath: string; agentSchemaSuffix?: string}[]> {
  const root = vscode.Uri.parse(workspaceUri).fsPath;
  const mainDir = path.join(root, 'knowledge', 'files');
  const agentsDir = path.join(root, 'agents');
  const files: { name: string; fullPath: string; agentSchemaSuffix?: string}[] = [];

  // main agent
  const mainFiles = await readKnowledgeFilesDir(mainDir);
  files.push(...mainFiles.map(f => ({
    name: f,
    fullPath: path.join(mainDir, f)
  })));

  // child agents
  try {
    const agentFolders = await fs.readdir(agentsDir);

    for (const agent of agentFolders) {
      const dir = path.join(agentsDir, agent, 'knowledge', 'files');
      const agentFiles = await readKnowledgeFilesDir(dir);

      files.push(...agentFiles.map(f => ({
        name: f,
        fullPath: path.join(dir, f),
        agentSchemaSuffix: agent
      })));
    }
  } catch {
    // agents folder may not exist
  }

  return files;
}