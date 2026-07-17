import * as vscode from 'vscode';
import { ChangeType } from '../types';
import { isKnowledgeFileChangeKind } from '../constants';

/**
 * A single local change to discard, projected from an SCM {@link Resource}.
 */
export interface DiscardChangeInput {
  /** Cached-file schema name used to fetch the last-synced baseline content. */
  schemaName: string;
  /** Whether the file was locally created, updated or deleted. */
  changeType: ChangeType;
  /** Server-side change kind (e.g. `FileAttachmentComponent` for knowledge files). */
  changeKind: string;
  /** Absolute on-disk URI of the file to restore or delete. */
  fileUri: vscode.Uri;
}

/** A change that could not be reverted offline, with a human-readable reason. */
export interface SkippedChange {
  path: string;
  reason: string;
}

/** Outcome of a discard operation. */
export interface DiscardResult {
  /** Files whose baseline content was rewritten (locally updated/deleted files). */
  restored: number;
  /** Locally-added files that were removed. */
  deleted: number;
  /** Changes that could not be reverted from the offline cache. */
  skipped: SkippedChange[];
}

/**
 * Dependencies injected into {@link discardLocalChanges} so the logic stays
 * testable without a live language server or real filesystem.
 */
export interface DiscardDependencies {
  /**
   * Returns the last-synced (cached) text content for a schema name.
   * Throws when the schema has no restorable cached representation
   * (e.g. the icon or a binary attachment), which callers treat as "skip".
   */
  getCachedContent: (schemaName: string) => Promise<string>;
  /** Writes text content to a file, creating parent directories as needed. */
  writeFile: (uri: vscode.Uri, content: string) => Promise<void>;
  /** Deletes a file. Missing files should resolve without throwing. */
  deleteFile: (uri: vscode.Uri) => Promise<void>;
}

/** File extensions whose content the text-only cached-file cache can restore. */
const TEXT_RESTORABLE_EXTENSIONS = ['.yml', '.yaml', '.fx1', '.json'];

/** Schema name of the agent icon, which is binary and not restorable offline. */
const ICON_SCHEMA_NAME = 'icon';

/**
 * Whether a locally updated/deleted file can be restored from the offline
 * cached-file cache. The cache only serves text (serialized YAML / workflow
 * JSON); the agent icon and knowledge blobs are binary and are re-downloaded
 * via `Get` instead.
 */
export function isTextRestorable(change: DiscardChangeInput): boolean {
  // Knowledge file attachments are binary blobs (their bytes are not cached),
  // regardless of the on-disk extension.
  if (isKnowledgeFileChangeKind(change.changeKind)) {
    return false;
  }
  if (change.schemaName.toLowerCase() === ICON_SCHEMA_NAME) {
    return false;
  }
  const lowerPath = change.fileUri.path.toLowerCase();
  return TEXT_RESTORABLE_EXTENSIONS.some(ext => lowerPath.endsWith(ext));
}

function fileNameOf(uri: vscode.Uri): string {
  return uri.path.split('/').pop() || uri.path;
}

/**
 * Reverts a set of local changes to their last-synced baseline:
 * - `Create` -> delete the locally-added file.
 * - `Update` / `Delete` -> rewrite the file from its cached baseline content.
 *
 * Changes that cannot be restored from the offline cache (icon / binary
 * attachments, or a cached content lookup failure) are collected in
 * {@link DiscardResult.skipped} rather than aborting the whole operation.
 */
export async function discardLocalChanges(
  changes: readonly DiscardChangeInput[],
  deps: DiscardDependencies,
): Promise<DiscardResult> {
  const result: DiscardResult = { restored: 0, deleted: 0, skipped: [] };

  for (const change of changes) {
    const displayPath = fileNameOf(change.fileUri);
    try {
      if (change.changeType === ChangeType.Create) {
        // Locally-added file with no cached baseline -> remove it. Safe for any
        // file type since we are only deleting a file that did not exist at last sync.
        await deps.deleteFile(change.fileUri);
        result.deleted++;
        continue;
      }

      // Update or Delete -> restore the last-synced content.
      if (!isTextRestorable(change)) {
        result.skipped.push({
          path: displayPath,
          reason: 'binary content (icon or knowledge file) can only be restored with Get',
        });
        continue;
      }

      const content = await deps.getCachedContent(change.schemaName);
      await deps.writeFile(change.fileUri, content);
      result.restored++;
    } catch (error) {
      result.skipped.push({ path: displayPath, reason: (error as Error).message });
    }
  }

  return result;
}
