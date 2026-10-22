const START = '<<<<<<<';
const SEPARATOR = '=======';
const END = '>>>>>>>';

export const hasConflictMarkers = (content: string): boolean =>
    content.split('\n').some(line => line.startsWith(START) || line.startsWith(END));

export interface ConflictBlock {
    startIndex: number;
    separatorIndex: number;
    endIndex: number;
    first: string[];
    second: string[];
}

export const findConflictBlocks = (content: string): ConflictBlock[] => {
    const lines = content.split('\n');
    const blocks: ConflictBlock[] = [];

    for (let index = 0; index < lines.length; index++) {
        if (!lines[index].startsWith(START)) {
            continue;
        }

        const separatorIndex = lines.findIndex((line, at) => at > index && line.trimEnd() === SEPARATOR);
        const endIndex = lines.findIndex((line, at) => at > index && line.startsWith(END));

        if (separatorIndex < 0 || endIndex < 0 || separatorIndex > endIndex) {
            throw new Error(`malformed conflict block starting at line ${index + 1}`);
        }

        blocks.push({
            startIndex: index,
            separatorIndex,
            endIndex,
            first: lines.slice(index + 1, separatorIndex),
            second: lines.slice(separatorIndex + 1, endIndex),
        });

        index = endIndex;
    }

    return blocks;
};

export interface ResolveOutcome {
    content: string;
    blocksResolved: number;
    keptSide: 'first' | 'second';
}

export const resolveKeepingValue = (content: string, expectedValue: string): ResolveOutcome => {
    const blocks = findConflictBlocks(content);

    if (blocks.length === 0) {
        throw new Error('no conflict block found to resolve');
    }

    const lines = content.split('\n');
    const resolved: string[] = [];
    let cursor = 0;
    let keptSide: 'first' | 'second' | undefined;

    for (const block of blocks) {
        const firstHas = block.first.some(line => line.includes(expectedValue));
        const secondHas = block.second.some(line => line.includes(expectedValue));

        if (firstHas === secondHas) {
            throw new Error(firstHas
                ? `both sides of the conflict block contain '${expectedValue}'`
                : `neither side of the conflict block contains '${expectedValue}' — the local edit did not survive the merge`);
        }

        const side = firstHas ? 'first' : 'second';

        if (keptSide && keptSide !== side) {
            throw new Error('conflict blocks disagree on which side carries the local value');
        }

        keptSide = side;
        resolved.push(...lines.slice(cursor, block.startIndex), ...(firstHas ? block.first : block.second));
        cursor = block.endIndex + 1;
    }

    resolved.push(...lines.slice(cursor));

    return { content: resolved.join('\n'), blocksResolved: blocks.length, keptSide: keptSide! };
};
