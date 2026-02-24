import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs/promises';
import { getAccessTokenByAccountId } from '../clients/account';
import { botComponentHandler } from '../botComponents/botComponentHandler';
import logger from '../services/logger';
import { AgentSyncInfo, Change, ChangeType } from '../types';
import { ChangeTrack } from './fileHelper';
import { TelemetryEventsKeys } from '../constants';

export function getFilePaths(workspaceUri: string): { root: string; filesDir: string; trackPath: string } {
  const root = vscode.Uri.parse(workspaceUri).fsPath;
  return {
    root,
    filesDir: path.join(root, 'knowledge', 'files'),
    trackPath: path.join(root, '.mcs', 'filechangetrack.json'),
  };
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
    await fs.rename(tempPath, fullPath);
  } catch (err: any) {
    if (err.code === 'EXDEV') {
      // If rename fails due to cross-device (like in linux arm64), copy instead
      try {
        await fs.copyFile(tempPath, fullPath);
        await fs.unlink(tempPath);
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
  const { trackPath, filesDir } = getFilePaths(workspaceUri);
  const trackingData = await readTrackingFile(trackPath);
  const localFiles = await readKnowledgeFilesDir(filesDir);
  if (!trackingData || !localFiles.length) {
    return [];
  }

  const changes: Change[] = [];
  const botHandler = await getDataverseBotHandler(syncInfo);
  const wsMeta = await botHandler.listWsComponentMetadata(syncInfo);
  const remoteFiles = new Set(wsMeta.map(metadata => decodeURIComponent(metadata.filename ?? '')));

  for (const fileName of localFiles) {
    const { localChangeType, schema } = trackingData[fileName] || {};
    const isRemoteFile = remoteFiles.has(fileName);
    if (!isRemoteFile || localChangeType !== undefined) {
      const fullPath = path.join('knowledge', 'files', fileName);
      const fileUri = vscode.Uri.file(fullPath).toString();
      changes.push({
        name: fileName,
        uri: fileUri,
        schemaName: schema ?? '',
        changeKind: 'knowledge',
        changeType: !isRemoteFile ? ChangeType.Create : localChangeType!
      });
    }
  }

  return changes;
}

export async function getKnowledgeRemoteChanges(syncInfo: AgentSyncInfo, workspaceUri: string): Promise<Change[]> {
  const { trackPath, filesDir } = getFilePaths(workspaceUri);
  const trackingData = await readTrackingFile(trackPath) || {};
  const localFiles = (await readKnowledgeFilesDir(filesDir)).map(file => file.toLowerCase());
  const uniqueLocalFiles = new Set(localFiles);

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
    if (!uniqueLocalFiles.has(fileName.toLowerCase())) {
      const fullPath = path.join(filesDir, fileName);
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
      const fullPath = path.join(filesDir, fileName);
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
