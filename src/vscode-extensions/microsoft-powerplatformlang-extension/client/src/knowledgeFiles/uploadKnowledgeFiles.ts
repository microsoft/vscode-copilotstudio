import * as vscode from 'vscode';
import * as fs from 'fs/promises';
import * as path from 'path';
import { CopilotStudioWorkspace } from '../sync/localWorkspaces';
import { isTextFile, loadChangeTrack, resolveConflict, saveChangeTrack } from './fileHelper';
import { generateSchemaNameForBotComponents } from '../botComponents/schemaName';
import { getDataverseBotHandler, getAllKnowledgeFiles, getTrackPath } from './syncUtils';
import { ConflictResolution, TelemetryEventsKeys } from '../constants';
import logger from '../services/logger';
import { ChangeType } from '../types';

export async function uploadKnowledgeFiles(ws: CopilotStudioWorkspace): Promise<void> {
  const { syncInfo, workspaceUri } = ws;
  if (!syncInfo || !syncInfo.dataverseEndpoint || !syncInfo.agentId) {
    return;
  }

  const trackPath = getTrackPath(workspaceUri);
  const knowledgeFiles = await getAllKnowledgeFiles(workspaceUri);
  const changeTrack = await loadChangeTrack(trackPath);
  const botHandler = await getDataverseBotHandler(syncInfo);
  const wsMeta = await botHandler.listWsComponentMetadata(syncInfo);

  const remoteFilenames = new Set(wsMeta.map(m => decodeURIComponent(m.filename ?? '')));
  const wsByFilename = new Map(wsMeta.map(m => [decodeURIComponent(m.filename ?? ''), m]));
  const wsBySchema = new Map(wsMeta.map(m => [m.schemaName, m]));
  const schemaNameSet = new Set<string>(wsBySchema.keys());

  for (const file in changeTrack) {
    const entry = changeTrack[file];
    const schemaStillExists = entry.schema ? wsBySchema.has(entry.schema) : false;

    if (!remoteFilenames.has(file) && entry.schema && !schemaStillExists) {
      entry.remoteChangeType = ChangeType.Delete;
    } else if (schemaStillExists && entry.remoteChangeType === ChangeType.Delete) {
      delete entry.remoteChangeType;
    }
  }
  const allFiles = knowledgeFiles
    .map(f => f.name)
    .filter(f => !f.endsWith('.mcs.yml'));

  const filePathMap = new Map<string, { fullPath: string; agentSchemaSuffix?: string }>(
    knowledgeFiles.map(f => [f.name, { fullPath: f.fullPath, agentSchemaSuffix: f.agentSchemaSuffix }])
  );

  const toUpload: { file: string; fullPath: string; localModifiedOn: number; size: number; botId: string; schema: string; isChildAgent: boolean }[] = [];
  const skipped: string[] = [];
  const conflicted: string[] = [];
  const filesInFolder = new Set(allFiles);
  const toDeleteRemote: { file: string; schema: string }[] = [];
  const deletedFiles: string[] = [];

  const botPrefix = await botHandler.getBotPrefix(syncInfo.agentId);
  const childAgents = botPrefix !== '' ? await botHandler.getChildAgents(syncInfo, botPrefix) : [];

  for (const file in changeTrack) {
    const entry = changeTrack[file];
    if (!filesInFolder.has(file)) {
      if (!entry.localChangeType) {
        entry.localChangeType = ChangeType.Delete;
        entry.localModifiedOn = 0;
      }
      if (entry.remoteChangeType !== ChangeType.Delete && entry.schema) {
        toDeleteRemote.push({ file, schema: entry.schema });
      }
    }
  }

  for (const file of allFiles) {
    const full = filePathMap.get(file);
    if (!full) {
      continue;
    }
    const fileStat = await fs.stat(full.fullPath);

    if (!fileStat.isFile() || fileStat.size > 128 * 1024 * 1024) {
      skipped.push(file);
      continue;
    }

    const remoteRec = wsByFilename.get(file);
    const prev = changeTrack[file];

    if (prev?.localChangeType === ChangeType.Delete && prev?.remoteChangeType === ChangeType.Delete) {
      delete changeTrack[file];
    } else if (prev?.remoteChangeType === ChangeType.Delete && wsBySchema.has(prev.schema ?? '')) {
      delete prev.remoteChangeType;
      delete prev.schema;
    } else if (prev?.remoteChangeType === ChangeType.Delete && prev.localChangeType !== ChangeType.Delete) {
      logger.logWarning(TelemetryEventsKeys.UploadKnowledgeFileWarning, `File <pii>"${file}"</pii> was deleted remotely but still exists locally. Please get changes before applying.`);
      skipped.push(file);
      continue;
    }

    const prevNow = changeTrack[file];
    const schema = remoteRec?.schemaName ?? prevNow?.schema ?? (() => {
      const generated = generateSchemaNameForBotComponents({
        botSchemaPrefix: botPrefix,
        componentPrefix: 'file',
        componentName: file,
        existingSchemaNames: Array.from(schemaNameSet),
      });
      schemaNameSet.add(generated);
      return generated;
    })();

    const childAgent = childAgents.find(c => full.agentSchemaSuffix?.length && c.schemaName.endsWith(full.agentSchemaSuffix));
    const agentId = childAgent?.id ?? await botHandler.getBotComponentId(file, schema, syncInfo.agentId);
    const agentSchemaName = childAgent?.schemaName ?? botPrefix;

    const botRec = schema ? wsBySchema.get(schema) : undefined;
    const remoteModifiedOn = botRec?.modifiedOn ?? 0;

    let needToUpload = false;

    if (!prevNow || (remoteModifiedOn && remoteModifiedOn !== prevNow.remoteModifiedOn)) {
      changeTrack[file] = {
        remoteModifiedOn,
        size: botRec?.sizeInBytes ?? fileStat.size,
        localModifiedOn: fileStat.mtimeMs,
        schema,
        agentId: agentId,
        agentSchemaName: agentSchemaName
      };

      if (!botRec) {
        needToUpload = true;
        changeTrack[file].localChangeType = ChangeType.Create;
      } else {
        const remoteContent = await botHandler.downloadKnowledgeFile(botRec.id);
        const result = await resolveConflict(file, full.fullPath, remoteContent, await isTextFile(file));
        if (result === ConflictResolution.UseLocal) {
          needToUpload = true;
          changeTrack[file].localChangeType = ChangeType.Update;
        } else if (result === ConflictResolution.Merge) {
          conflicted.push(file);
          changeTrack[file].localChangeType = ChangeType.Update;
          continue;
        } else {
          skipped.push(file);
          continue;
        }
      }
    } else if (
      prevNow.localModifiedOn !== fileStat.mtimeMs ||
      prevNow.size !== fileStat.size ||
      !botRec
    ) {
      needToUpload = true;
      changeTrack[file].localChangeType = ChangeType.Update;
    }

    if (needToUpload) {
      const botId = botRec
        ? botRec.id
        : agentId;
      toUpload.push({ file, fullPath: full.fullPath, localModifiedOn: fileStat.mtimeMs, size: fileStat.size, botId, schema, isChildAgent: childAgent?.id ? true : false });
    }

    if (changeTrack[file]?.localChangeType === ChangeType.Delete) {
      delete changeTrack[file].localChangeType;
    }

    if (changeTrack[file]?.remoteChangeType === ChangeType.Delete) {
      delete changeTrack[file].remoteChangeType;
    }
  }

  for (const { file, schema } of toDeleteRemote) {
    const botRec = schema ? wsBySchema.get(schema) : undefined;
    if (botRec) {
      await botHandler.deleteBotComponent(botRec.id);
      changeTrack[file].remoteChangeType = ChangeType.Delete;
      deletedFiles.push(file);
    }
  }

  await saveChangeTrack(trackPath, changeTrack);

  if (!toUpload.length) {
    if (deletedFiles.length) {
      logger.logInfo(TelemetryEventsKeys.UploadKnowledgeFileSuccess, `Files deleted remotely: <pii>${deletedFiles.join(', ')}</pii>`);
    }
    return;
  }

  const cancelSrc = new vscode.CancellationTokenSource();
  let canceled = false;
  const failedUploads: string[] = [];
  const successfulUploads: string[] = [];

  await vscode.window.withProgress({
    location: vscode.ProgressLocation.Notification,
    title: 'Uploading knowledge files…',
    cancellable: true
  }, async (progress, token) => {
    token.onCancellationRequested(() => {
      canceled = true;
      cancelSrc.cancel();
    });

    for (let i = 0; i < toUpload.length; i++) {
      if (cancelSrc.token.isCancellationRequested) {
        break;
      }

      const { file, fullPath, localModifiedOn, size, botId: initialBotId, schema: initialSchema, isChildAgent } = toUpload[i];
      let success = false;
      let botId = initialBotId;
      let schema = initialSchema;

      try {
        if (!wsBySchema.has(schema) && botId !== syncInfo.agentId && isChildAgent) {
          botId = await botHandler.createBotComponent(file, schema, botId, syncInfo.agentId, isChildAgent);
        }

        const url = new URL(`/api/data/v9.2/botcomponents(${botId})/filedata/`, syncInfo.dataverseEndpoint);
        const data = await fs.readFile(fullPath);
        const headers = {
          Authorization: `Bearer ${botHandler['accessToken']}`,
          Accept: 'application/json',
          'OData-Version': '4.0',
          'OData-MaxVersion': '4.0',
          'x-ms-file-name': file,
          'Content-Type': 'application/octet-stream',
          'Content-Length': String(data.length),
        };

        const response = await botHandler.dataverseHttpRequest({
          url,
          method: 'PATCH',
          body: data,
          extraHeaders: headers
        });

        if (response.statusCode >= 400) {
          let details = '';

          try {
            const text = response.body.toString('utf8');
            const json = JSON.parse(text);
            details = json?.error?.message ?? text;
          } catch (parseErr) {
            details = ` - Raw: ${response.body.toString('utf8')}`;
          }

          logger.logError(TelemetryEventsKeys.UploadKnowledgeFileError, undefined, { message: `Failed to upload <pii>${file}</pii>: ${response.statusCode}${details}` });
          throw new Error(`Failed to upload ${file}: ${response.statusCode}${details}`);
        }

        const updatedMeta = await botHandler.listWsComponentMetadata(syncInfo);
        const updated = updatedMeta.find(m => m.schemaName === schema);
        const existing = changeTrack[file] ?? {};

        changeTrack[file] = {
          ...existing,
          remoteModifiedOn: updated?.modifiedOn ?? 0,
          size: updated?.sizeInBytes ?? size,
          localModifiedOn,
          schema,
          agentId: updated?.agentId ?? existing.agentId,
          agentSchemaName: updated?.agentSchemaName
        };
        
        delete changeTrack[file].localChangeType;
        delete changeTrack[file].remoteChangeType;

        success = true;
      } catch (err) {
        failedUploads.push(file);
        logger.logError(TelemetryEventsKeys.UploadKnowledgeFileError, `Failed to upload <pii>${file}</pii>: ${err}`);
      }


      if (success) {
        successfulUploads.push(file);
      }

      progress.report({
        message: `${Math.round((i + 1) / toUpload.length * 100)}% uploaded`,
        increment: 100 / toUpload.length
      });
    }
  });

  await saveChangeTrack(trackPath, changeTrack);

  if (canceled) {
    logger.logWarning(TelemetryEventsKeys.UploadKnowledgeFileWarning, 'Upload cancelled by user.');
  } else {
    logger.logInfo(
      TelemetryEventsKeys.UploadKnowledgeFileSuccess,
      [
        successfulUploads.length ? `Uploaded (${successfulUploads.length}): <pii>${successfulUploads.join(', ')}</pii>` : null,
        failedUploads.length ? `Failed (${failedUploads.length}): <pii>${failedUploads.join(', ')}</pii>` : null,
        conflicted.length ? `Conflicted (${conflicted.length}): <pii>${conflicted.join(', ')}</pii>` : null,
        skipped.length ? `Skipped (${skipped.length}): <pii>${skipped.join(', ')}</pii>` : null,
        deletedFiles.length ? `Deleted remotely (${deletedFiles.length}): <pii>${deletedFiles.join(', ')}</pii>` : null,
      ].filter(Boolean).join('. ')
    );
  }
}
