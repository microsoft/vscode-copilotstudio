import * as path from 'path';
import { CopilotStudioWorkspace, WorkspaceType } from '../sync/localWorkspaces';

export type ReattachPlan = {
  workspaces: CopilotStudioWorkspace[];
  missingCollectionDirectories: string[];
};

export type CollectionCandidate = {
  workspace: CopilotStudioWorkspace;
  folderPath: string;
};

export const normalizePathForComparison = (value: string): string => {
  const normalized = stripTrailingSeparators(value);
  return process.platform === 'win32' || process.platform === 'darwin' ? normalized.toLowerCase() : normalized;
};

const stripTrailingSeparators = (value: string): string => path.normalize(value).replace(/[\\/]+$/, '');

export const parseReferencedDirectories = (referencesContent: string): string[] => {
  const directoryPattern = /^[ \t]*-?[ \t]*directory:[ \t]*(.+?)[ \t]*$/gm;
  const directories: string[] = [];
  for (const match of referencesContent.matchAll(directoryPattern)) {
    const referencedDirectory = match[1].trim().replace(/^['"]|['"]$/g, '');
    if (referencedDirectory) {
      directories.push(referencedDirectory);
    }
  }
  return directories;
};

export const buildReattachPlanCore = (workspace: CopilotStudioWorkspace, workspaceFolder: string, referencesContent: string | undefined, candidateCollections: readonly CollectionCandidate[]): ReattachPlan => {
  if (workspace.type !== WorkspaceType.Agent || referencesContent === undefined) {
    return { workspaces: [workspace], missingCollectionDirectories: [] };
  }

  const collectionWorkspaces: CopilotStudioWorkspace[] = [];
  const missingCollectionDirectories: string[] = [];
  const seenDirectories = new Set<string>();
  const findCollection = (matches: (folderPath: string) => boolean): CollectionCandidate | undefined => candidateCollections.find(candidate => candidate.workspace.type === WorkspaceType.ComponentCollection && matches(candidate.folderPath));
  for (const referencedDirectory of parseReferencedDirectories(referencesContent)) {
    const resolvedDirectory = path.resolve(workspaceFolder, referencedDirectory);
    const exactResolvedDirectory = stripTrailingSeparators(resolvedDirectory);
    const normalizedResolvedDirectory = normalizePathForComparison(resolvedDirectory);
    if (seenDirectories.has(normalizedResolvedDirectory)) {
      continue;
    }
    seenDirectories.add(normalizedResolvedDirectory);
    const collectionCandidate = findCollection(folderPath => stripTrailingSeparators(folderPath) === exactResolvedDirectory)
      ?? findCollection(folderPath => normalizePathForComparison(folderPath) === normalizedResolvedDirectory);
    if (collectionCandidate) {
      collectionWorkspaces.push(collectionCandidate.workspace);
    } else {
      missingCollectionDirectories.push(resolvedDirectory);
    }
  }

  return { workspaces: [workspace, ...collectionWorkspaces], missingCollectionDirectories };
};
