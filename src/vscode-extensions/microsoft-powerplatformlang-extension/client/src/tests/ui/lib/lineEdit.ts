import * as fs from 'node:fs';

export const findLineIndex = (lines: readonly string[], key: string): number =>
    lines.findIndex(line => line.trimStart().startsWith(key) || line.includes(key));

export const readLine = (filePath: string, key: string): string => {
    const lines = fs.readFileSync(filePath, 'utf8').split('\n');
    const index = findLineIndex(lines, key);

    if (index < 0) {
        throw new Error(`no line starting '${key}' in ${filePath}`);
    }

    return lines[index];
};

export const replaceLine = (filePath: string, key: string, value: string): string => {
    const content = fs.readFileSync(filePath, 'utf8');
    const newline = content.includes('\r\n') ? '\r\n' : '\n';
    const lines = content.split(/\r?\n/);
    const index = findLineIndex(lines, key);

    if (index < 0) {
        throw new Error(`no line starting '${key}' in ${filePath}`);
    }

    const original = lines[index];
    const indent = original.slice(0, original.length - original.trimStart().length);
    lines[index] = `${indent}${value}`;
    fs.writeFileSync(filePath, lines.join(newline), 'utf8');

    return original;
};

export const valueOf = (line: string, key: string): string =>
    key.endsWith(':') ? line.trimStart().slice(key.length).trim() : line.trimStart();
