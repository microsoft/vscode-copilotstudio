import { CustomConnectorIconFileExtension, isConnectorIconSchemaName } from '../constants';

/** Builds the path of a cached or remote virtual file, giving a connector icon a PNG extension so its diff opens in the image preview. */
export function toVirtualFilePath(label: string, schemaName: string): string {
	return `/${label}: ${schemaName}${isConnectorIconSchemaName(schemaName) ? CustomConnectorIconFileExtension : ''}`;
}

/** Reads the schema name back from the path of a cached or remote virtual file. */
export function toSchemaName(label: string, path: string): string {
	const schemaName = path.startsWith(`/${label}: `) ? path.substring(label.length + 3) : path.substring(1);
	const iconSchemaName = schemaName.slice(0, -CustomConnectorIconFileExtension.length);
	return schemaName.endsWith(CustomConnectorIconFileExtension) && isConnectorIconSchemaName(iconSchemaName) ? iconSchemaName : schemaName;
}

/** Converts the text the language server returns for a file into its bytes, decoding the base64 a connector icon is sent as. */
export function toFileBytes(schemaName: string, content: string | undefined): Uint8Array {
	return Buffer.from(content ?? '', isConnectorIconSchemaName(schemaName) ? 'base64' : 'utf8');
}
