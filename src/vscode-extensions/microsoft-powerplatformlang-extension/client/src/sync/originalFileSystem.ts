import { Uri, FileSystemProvider, FileChangeEvent, FileType, EventEmitter, FileStat } from "vscode";
import { GetFileRequest, GetFileResponse } from "../types";
import { getWorkspaceByUri } from "./localWorkspaces";
import { lspClient } from "../services/lspClient";
import { LspMethods, TelemetryEventsKeys } from "../constants";
import logger, { formatPii, PiiRedactionType } from "../services/logger";
import { toFileBytes, toSchemaName } from "./virtualFilePath";

export const LOCAL_STATE_SCHEME = "mcs";

export class OriginalFileSystem implements FileSystemProvider {
    private _onDidChangeFile = new EventEmitter<FileChangeEvent[]>();
    readonly onDidChangeFile = this._onDidChangeFile.event;

    watch(): any { return { dispose: () => {} }; }

    async stat(uri: Uri): Promise<FileStat> {
        return {
            type: FileType.File,
            ctime: 0,
            mtime: Date.now(),
            size: 0
        };
    }

    async readFile(uri: Uri): Promise<Uint8Array> {
        let agentName: string | undefined;
        try {
            const workspace = getWorkspaceByUri(uri);
            if (!workspace) {
                logger.logError(TelemetryEventsKeys.GetLocalFileError, undefined, {
                    message: `Error fetching file: could not locate workspace for file ${formatPii(String(uri), PiiRedactionType.FileUri)}`
                });
                return new Uint8Array();
            }
            agentName = workspace.displayName;

            const { workspaceUri } = workspace;
            const schemaName = toSchemaName("Local Cache", uri.path);

            const request: GetFileRequest = { workspaceUri, schemaName };
            const result = await lspClient.sendRequest<GetFileResponse>(LspMethods.GET_CACHED_FILE, request);

            return toFileBytes(schemaName, result.content);
        } catch (error) {
            logger.logError(TelemetryEventsKeys.GetLocalFileError, 'Error fetching file', { error });
            return new Uint8Array();
        }
    }

    readDirectory(): Promise<[string, FileType][]> { throw new Error("Not implemented"); }
    createDirectory(): void { throw new Error("Not implemented"); }
    writeFile(): void { throw new Error("Not implemented"); }
    delete(): void { throw new Error("Not implemented"); }
    rename(): void { throw new Error("Not implemented"); }
}