import * as fs from 'node:fs';
import * as path from 'node:path';
import * as crypto from 'node:crypto';

export const relativePosix = (root: string, target: string): string => path.relative(root, target).replace(/\\/g, '/');

export const listFiles = (root: string, skipDirectories: readonly string[] = ['.mcs']): string[] => {
    const found: string[] = [];

    const walk = (directory: string): void => {
        for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
            const candidate = path.join(directory, entry.name);

            if (entry.isDirectory()) {
                if (!skipDirectories.includes(entry.name)) {
                    walk(candidate);
                }
            } else {
                found.push(candidate);
            }
        }
    };

    walk(root);
    return found.sort();
};

export const listDirectories = (root: string, skipDirectories: readonly string[] = []): string[] => {
    const found: string[] = [];

    const walk = (directory: string): void => {
        for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
            if (entry.isDirectory() && !skipDirectories.includes(entry.name)) {
                const candidate = path.join(directory, entry.name);
                found.push(candidate);
                walk(candidate);
            }
        }
    };

    walk(root);
    return found;
};

export const fileSet = (root: string, skipDirectories?: readonly string[]): string[] =>
    listFiles(root, skipDirectories).map(filePath => relativePosix(root, filePath));

export const hashTree = (root: string, skipDirectories?: readonly string[]): Map<string, string> => {
    const hashes = new Map<string, string>();

    for (const filePath of listFiles(root, skipDirectories)) {
        hashes.set(relativePosix(root, filePath), crypto.createHash('sha256').update(fs.readFileSync(filePath)).digest('hex'));
    }

    return hashes;
};

export const diffHashes = (before: Map<string, string>, after: Map<string, string>): string[] => {
    const differences: string[] = [];

    for (const [relative, hash] of before) {
        const current = after.get(relative);
        if (current === undefined) {
            differences.push(`removed ${relative}`);
        } else if (current !== hash) {
            differences.push(`changed ${relative}`);
        }
    }

    for (const relative of after.keys()) {
        if (!before.has(relative)) {
            differences.push(`added ${relative}`);
        }
    }

    return differences.sort();
};

export const findAgentRoot = (root: string): string => {
    if (fs.existsSync(path.join(root, 'settings.mcs.yml'))) {
        return root;
    }

    const candidates = fs.readdirSync(root, { withFileTypes: true })
        .filter(entry => entry.isDirectory())
        .map(entry => path.join(root, entry.name))
        .filter(candidate => fs.existsSync(path.join(candidate, 'settings.mcs.yml')));

    if (candidates.length !== 1) {
        throw new Error(`expected exactly one agent root under ${root}, found ${candidates.length}`);
    }

    return candidates[0];
};
