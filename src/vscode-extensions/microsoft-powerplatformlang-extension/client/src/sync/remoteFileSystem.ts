import { Uri, FileSystemProvider, FileChangeEvent, FileType, EventEmitter, Event, FileStat } from "vscode";
import { RemoteFileRequest, GetFileResponse } from '../types';
import { findWorkspaceForUri } from './localWorkspaces';
import { lspClient, buildLspRequestPayload } from "../services/lspClient";
import { LspMethods, TelemetryEventsKeys } from "../constants";
import logger from "../services/logger";

export const REMOTE_STATE_SCHEME = 'mcs-remote';

export class RemoteFileSystem implements FileSystemProvider {
    private _onDidChangeFile = new EventEmitter<FileChangeEvent[]>();
    readonly onDidChangeFile: Event<FileChangeEvent[]> = this._onDidChangeFile.event;

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
        try {
            const workspace = findWorkspaceForUri(uri.query);
            if (!workspace) {
                logger.logError(TelemetryEventsKeys.GetRemoteFileError, undefined, { message: `Error fetching file: could not locate workspace for file <pii>${uri}</pii>` });                
                return new Uint8Array();
            }

            const { syncInfo, workspaceUri } = workspace;
            if (!syncInfo) {
                logger.logError(TelemetryEventsKeys.GetRemoteFileError, `Error fetching file: connection file .mcs::conn.json is missing, please clone again.`);
                return new Uint8Array();
            }

            let schemaName = uri.path.substring(1); // remove leading slash
            if (schemaName.startsWith("Remote: ")) {
                schemaName = schemaName.substring("Remote: ".length);
            }

            const request: RemoteFileRequest = {
                ...(await buildLspRequestPayload(syncInfo)),
                schemaName: schemaName,
                workspaceUri
            };

            const result = await lspClient.sendRequest<GetFileResponse>(LspMethods.GET_REMOTE_FILE, request);
            return Buffer.from(result.content ?? '', 'utf8');
        } catch (error) {
            logger.logError(TelemetryEventsKeys.GetRemoteFileError, `Error fetching file: ${(error as Error).message}`);
            return new Uint8Array();
        }
    }

    readDirectory(): Promise<[string, FileType][]> { throw new Error("Not implemented"); }
    createDirectory(): void { throw new Error("Not implemented"); }
    writeFile(): void { throw new Error("Not implemented"); }
    delete(): void { throw new Error("Not implemented"); }
    rename(): void { throw new Error("Not implemented"); }
}