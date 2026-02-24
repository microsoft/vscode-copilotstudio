// The module 'vscode' contains the VS Code extensibility API
// Import the module and reference it with the alias vscode in your code below
import {Uri, SourceControlResourceState, SourceControlResourceDecorations, ThemeColor, FileDecoration, ThemeIcon, Command } from "vscode";
import * as models from '../types';
import path from "path";
import { LOCAL_STATE_SCHEME } from "./originalState";
import { REMOTE_STATE_SCHEME } from "./remoteState";

const iconsRootPath = path.join(path.dirname(__dirname), 'icons');
function getIconUri(iconName: string, theme: string): Uri {
	return Uri.file(path.join(iconsRootPath, theme, `${iconName}.svg`));
}

export class Resource implements SourceControlResourceState {

	static getStatusLetter(type: models.ChangeType): string {
		switch (type) {
			case models.ChangeType.Create: return 'A';
			case models.ChangeType.Delete: return 'D';
			case models.ChangeType.Update: return 'M';
			default: return '';
		}
	}

	static getStatusText(type: models.ChangeType) {
		switch (type) {
			case models.ChangeType.Create: return 'Added';
			case models.ChangeType.Delete: return 'Deleted';
			case models.ChangeType.Update: return 'Modified';
			default: return '';
		}
	}

	static getStatusColor(type: models.ChangeType): ThemeColor {
		switch (type) {
			case models.ChangeType.Update:
				return new ThemeColor('gitDecoration.modifiedResourceForeground');
			case models.ChangeType.Delete:
				return new ThemeColor('gitDecoration.deletedResourceForeground');
			case models.ChangeType.Create:
				return new ThemeColor('gitDecoration.addedResourceForeground');
			default:
				throw new Error('Unknown git status: ' + type);
		}
	}

	get resourceUri(): Uri {
		return this._resourceUri;
	}

	get type(): models.ChangeType { return this._type; }
	get schemaName(): string { return this._schemaName; }

	get command() : Command {
		return this._commandResolver.resolveDefaultCommand(this);
	}

	private get tooltip(): string {
		return Resource.getStatusText(this.type);
	}

	private get strikeThrough(): boolean {
		switch (this.type) {
			case models.ChangeType.Delete:
				return true;
			default:
				return false;
		}
	}

	private get faded(): boolean {
		return false;
	}

	get decorations(): SourceControlResourceDecorations {
		const light = {iconPath: this.getIconPath('light')};
		const dark =  {iconPath: this.getIconPath('dark')};
		const tooltip = this.tooltip;
		const strikeThrough = this.strikeThrough;
		const faded = this.faded;
		return { strikeThrough, faded, tooltip,  light, dark };
	}

	get fullResourceUri(): Uri {
		return this._commandResolver.getFullUri(this);
	}

	get letter(): string {
		return Resource.getStatusLetter(this.type);
	}

	get color(): ThemeColor {
		return Resource.getStatusColor(this.type);
	}

	get priority(): number {
		return 1;
	}

	private getIconPath(theme: string): ThemeIcon {
		switch (this._type) {
			case models.ChangeType.Update: return Resource.Icons[theme].Modified;
			case models.ChangeType.Delete: return Resource.Icons[theme].Deleted;
			case models.ChangeType.Create: return Resource.Icons[theme].Added;
			default:
				throw new Error('Unknown git status: ' + this._type);
		}
	}

	get originalResourceUri(): Uri{
		return this._commandResolver.getOriginalUri(this);
	}

	get resourceDecoration() : FileDecoration {
		const res = new FileDecoration(this.letter, this.tooltip, this.color);
		res.propagate = this.type !== models.ChangeType.Delete;
		return res;
	}
	
	get changeKind(): string {
		return this._kind;
	}

	constructor(
		private _commandResolver: ResourceCommandResolver,
		private _resourceUri: Uri,
		private _schemaName: string,
		private _kind : string,
		private _type: models.ChangeType,
	) { }
	
	private static Icons: any = {
		light: {
			Modified: getIconUri('status-modified', 'light'),
			Added: getIconUri('status-added', 'light'),
			Deleted: getIconUri('status-deleted', 'light'),
			Renamed: getIconUri('status-renamed', 'light'),
			Copied: getIconUri('status-copied', 'light'),
			Untracked: getIconUri('status-untracked', 'light'),
			Ignored: getIconUri('status-ignored', 'light'),
			Conflict: getIconUri('status-conflict', 'light'),
			TypeChanged: getIconUri('status-type-changed', 'light')
		},
		dark: {
			Modified: getIconUri('status-modified', 'dark'),
			Added: getIconUri('status-added', 'dark'),
			Deleted: getIconUri('status-deleted', 'dark'),
			Renamed: getIconUri('status-renamed', 'dark'),
			Copied: getIconUri('status-copied', 'dark'),
			Untracked: getIconUri('status-untracked', 'dark'),
			Ignored: getIconUri('status-ignored', 'dark'),
			Conflict: getIconUri('status-conflict', 'dark'),
			TypeChanged: getIconUri('status-type-changed', 'dark')
		}
	};
}


export interface ResourceCommandResolver{
	resolveDefaultCommand(resource: Resource): Command;
	getFullUri(resource: Resource): Uri;
	getOriginalUri(resource: Resource): Uri;
}

export class LocalChangeResourceCommandResolver implements ResourceCommandResolver {

	constructor(private _uri: Uri) { }

	resolveDefaultCommand(resource: Resource): Command {
		switch (resource.type) {
			case models.ChangeType.Create:
				return resolveDiffCommand(getEmptyUri(), this.getFullUri(resource), resource.resourceUri);
			case models.ChangeType.Delete:
				return resolveDiffCommand(this.getOriginalUri(resource), getEmptyUri(), resource.resourceUri);
			default:
				return resolveDiffCommand(this.getOriginalUri(resource), this.getFullUri(resource), resource.resourceUri);
		}
	}


	getFullUri(resource: Resource): Uri {
		return  Uri.joinPath(this._uri, resource.resourceUri.path);
	}

	getOriginalUri(resource: Resource): Uri {	
		return getLocalCacheUri(resource.schemaName, this._uri.toString());
	}

}

export class RemoteChangeResourceCommandResolver implements ResourceCommandResolver{

	constructor(private _uri: Uri) { }

	resolveDefaultCommand(resource: Resource): Command {
		switch (resource.type) {
			case models.ChangeType.Create:
				return resolveDiffCommand(getEmptyUri(), this.getFullUri(resource), resource.resourceUri);
			case models.ChangeType.Delete:
				return resolveDiffCommand(this.getOriginalUri(resource), getEmptyUri(), resource.resourceUri);
			default:
				return resolveDiffCommand(this.getOriginalUri(resource), this.getFullUri(resource), resource.resourceUri);
		}
	}

	getFullUri(resource: Resource): Uri {
		return getRemoteUri(resource.schemaName, this._uri.toString());
	}

	getOriginalUri(resource: Resource): Uri {	
		return getLocalCacheUri(resource.schemaName, this._uri.toString());
	}
}

function resolveDiffCommand(original: Uri, full: Uri, current: Uri): Command {
		return {
			command: 'vscode.diff',
			title: 'Open',
			arguments: [original, full, current]
		};
	}

function getEmptyUri(): Uri {
		return Uri.from({ scheme: LOCAL_STATE_SCHEME, authority: "empty", path: "/" });
	}


function getLocalCacheUri(schemaName: string, uri: string): Uri {	
		return Uri.from({ scheme: LOCAL_STATE_SCHEME, authority: "local", path: "/" + schemaName, query: uri });
	}

function getRemoteUri(schemaName: string, uri: string): Uri {	
		return Uri.from({ scheme: REMOTE_STATE_SCHEME, authority: "remote", path: "/" + schemaName, query: uri });
	}