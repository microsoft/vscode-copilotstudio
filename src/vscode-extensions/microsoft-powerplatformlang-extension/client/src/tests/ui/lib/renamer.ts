import * as fs from 'node:fs';
import * as path from 'node:path';
import { listDirectories, listFiles } from './tree';

const BINARY_EXTENSIONS = ['.png', '.zip', '.jpg', '.jpeg', '.gif', '.ico', '.pdf', '.docx', '.xlsx', '.pptx'];

export const isBinaryPath = (filePath: string): boolean => BINARY_EXTENSIONS.includes(path.extname(filePath).toLowerCase());

export const readScalar = (filePath: string, key: string): string | undefined => {
    const match = fs.readFileSync(filePath, 'utf8').split('\n').find(line => line.trimStart().startsWith(`${key}:`));
    return match?.trimStart().slice(key.length + 1).trim() || undefined;
};

export const readSchemaName = (identityRoot: string): string => {
    const settingsPath = path.join(identityRoot, 'settings.mcs.yml');

    if (!fs.existsSync(settingsPath)) {
        throw new Error(`settings.mcs.yml not found at ${identityRoot}`);
    }

    const schemaName = readScalar(settingsPath, 'schemaName');

    if (!schemaName) {
        throw new Error(`schemaName not found in ${settingsPath}`);
    }

    return schemaName;
};

const ordered = (replacements: ReadonlyMap<string, string>): [string, string][] =>
    [...replacements].sort(([left], [right]) => right.length - left.length);

const rewrite = (value: string, replacements: readonly [string, string][]): string =>
    replacements.reduce((text, [from, to]) => text.split(from).join(to), value);

export const applyReplacements = (root: string, replacements: ReadonlyMap<string, string>): number => {
    const pairs = ordered(replacements);
    let changed = 0;

    for (const filePath of listFiles(root, [])) {
        if (isBinaryPath(filePath)) {
            continue;
        }

        const content = fs.readFileSync(filePath, 'utf8');
        const rewritten = rewrite(content, pairs);

        if (rewritten !== content) {
            fs.writeFileSync(filePath, rewritten, 'utf8');
            changed++;
        }
    }

    return changed;
};

export const renamePaths = (root: string, replacements: ReadonlyMap<string, string>): void => {
    const pairs = ordered(replacements);

    const move = (candidate: string): void => {
        const name = path.basename(candidate);
        const renamed = rewrite(name, pairs);

        if (renamed !== name) {
            fs.renameSync(candidate, path.join(path.dirname(candidate), renamed));
        }
    };

    for (const directory of listDirectories(root).sort((left, right) => right.length - left.length)) {
        move(directory);
    }

    for (const filePath of listFiles(root, [])) {
        move(filePath);
    }
};

const setScalar = (filePath: string, key: string, value: string): void => {
    const lines = fs.readFileSync(filePath, 'utf8').split('\n');
    const index = lines.findIndex(line => line.trimStart().startsWith(`${key}:`));

    if (index < 0) {
        throw new Error(`${key} not found in ${filePath}`);
    }

    const indent = lines[index].slice(0, lines[index].length - lines[index].trimStart().length);
    lines[index] = `${indent}${key}: ${value}`;
    fs.writeFileSync(filePath, lines.join('\n'), 'utf8');
};

export interface RenameOutcome {
    originalIdentity: string;
    identity: string;
    displayName: string;
    filesRewritten: number;
}

export const renameWorkspace = (identityRoot: string, contentRoot: string, schemaName: string, displayName: string): RenameOutcome => {
    const originalSchemaName = readSchemaName(identityRoot);

    if (originalSchemaName === schemaName) {
        return { originalIdentity: originalSchemaName, identity: schemaName, displayName, filesRewritten: 0 };
    }

    const replacements = new Map<string, string>([[originalSchemaName, schemaName]]);
    const filesRewritten = applyReplacements(contentRoot, replacements);
    renamePaths(contentRoot, replacements);
    setScalar(path.join(identityRoot, 'settings.mcs.yml'), 'displayName', displayName);

    return { originalIdentity: originalSchemaName, identity: schemaName, displayName, filesRewritten };
};

export const copyDirectory = (source: string, destination: string): void => {
    fs.mkdirSync(destination, { recursive: true });

    for (const entry of fs.readdirSync(source, { withFileTypes: true })) {
        const from = path.join(source, entry.name);
        const to = path.join(destination, entry.name);

        if (entry.isDirectory()) {
            copyDirectory(from, to);
        } else {
            fs.copyFileSync(from, to);
        }
    }
};
