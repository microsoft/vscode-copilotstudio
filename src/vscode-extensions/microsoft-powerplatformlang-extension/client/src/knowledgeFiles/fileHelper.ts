import * as vscode from 'vscode';
import * as fs from 'fs/promises';
import * as path from 'path';
import * as os from 'os';
import { randId } from '../botComponents/schemaName';
import { FileDiff } from './fileDiff';
import { safeSaveFile } from './syncUtils';
import { ConflictResolution } from '../constants';
import { ChangeType } from '../types';

export interface FileMetadata {  
  schema?: string;
  size: number;             
  remoteModifiedOn?: number;
  remoteChangeType?: ChangeType;
  localModifiedOn?: number;
  localChangeType?: ChangeType;
}

export type ChangeTrack = Record<string, FileMetadata>;

export async function isTextFile(filename: string): Promise<boolean> {
  const textExt = new Set(['.txt','.json','.yaml','.yml','.js','.ts','.md']);
  return textExt.has(path.extname(filename).toLowerCase());
}

export async function loadChangeTrack(trackPath: string): Promise<ChangeTrack> {
  try {
    const json = await fs.readFile(trackPath, 'utf8');
    return JSON.parse(json);
  } catch {
    return {};
  }
}

export async function saveChangeTrack(trackPath: string, changeTrack: ChangeTrack) {
  const dir = path.dirname(trackPath);
  await fs.mkdir(dir, { recursive: true });
  const tempPath = path.join(os.tmpdir(), `.mcs-changetrack-${Date.now()}-${randId(3)}.json`);
  const content = JSON.stringify(changeTrack, null, 2);
  await safeSaveFile(trackPath, tempPath, Buffer.from(content, 'utf8'));  
}

export async function resolveConflict(
  filename: string,
  localPath: string,
  remoteContent: string | Buffer,
  canMerge: boolean
): Promise<typeof ConflictResolution[keyof typeof ConflictResolution] | undefined> {
  const options = [ConflictResolution.UseRemote, canMerge ? ConflictResolution.Merge : undefined, ConflictResolution.UseLocal].filter(Boolean) as string[];
  const choice = await vscode.window.showWarningMessage(`Conflict detected for file ${filename}.`, { modal: true }, ...options);

  switch (choice) {
    case ConflictResolution.UseLocal:
      return ConflictResolution.UseLocal;
    case ConflictResolution.UseRemote:
      return ConflictResolution.UseRemote;
    case ConflictResolution.Merge: {
      const localContent = await fs.readFile(localPath, 'utf8');
      const merged = fileMerge(localContent, typeof remoteContent === 'string' ? remoteContent : remoteContent.toString('utf8'));
      const tempPath = path.join(os.tmpdir(), `.mcs-merge-${filename}-${Date.now()}-${randId(3)}.txt`);
      await fs.writeFile(tempPath, merged, 'utf8');
      await fs.rename(tempPath, localPath);
      const doc = await vscode.workspace.openTextDocument(localPath);
      await vscode.window.showTextDocument(doc);
      return ConflictResolution.Merge;
    }
    default:
      return undefined;
  }
}

export function fileMerge(local: string, remote: string): string {
  const fileDiff = new FileDiff(local, remote);
  const diff = fileDiff.computeDiff();
  
  if (diff.length === 0) {
    return local;
  }

  let merged = '';
  let i = 0;

  while (i < diff.length) {
    const part = diff[i];

    if (part.removed) {
      // Get all consecutive removed lines
      let removedLines = '';
      let j = i;
      while (j < diff.length && diff[j].removed) {
        removedLines += diff[j].value;
        j++;
      }

      // Get all consecutive added lines
      let addedLines = '';
      let k = j;
      while (k < diff.length && diff[k].added) {
        addedLines += diff[k].value;
        k++;
      }

      if (!(removedLines.trim() === '' && addedLines.trim() === '')) {
        merged += `<<<<<<< LOCAL\n`;
        merged += removedLines;
        if (!removedLines.endsWith('\n')) {
          merged += '\n';
        }
        merged += `=======\n`;
        merged += addedLines;
        if (!addedLines.endsWith('\n')) {
          merged += '\n';
        }
        merged += `>>>>>>> REMOTE\n`;
      } else {
        merged += removedLines + addedLines;
      }

      i = k;
    } else if (part.added) {
      // Get all consecutive added lines
      merged += part.value;
      i++;
    } else {
      // Unchanged content
      merged += part.value;
      i++;
    }
  }

  return merged;
}

