import * as fs from 'node:fs';
import * as path from 'node:path';
import { applyReplacements, copyDirectory, readSchemaName } from './renamer';
import { listFiles, relativePosix } from './tree';
import type { ShapeDescriptor } from './shapes';

export interface ComponentFact {
    kind: string;
    relativePath: string;
    cloudName: string;
    expectMaterialisedMetadata?: string;
}

const listFilesIn = (directory: string, recursive = false): string[] =>
    fs.existsSync(directory) ? (recursive ? listFiles(directory, []) : fs.readdirSync(directory, { withFileTypes: true }).filter(entry => entry.isFile()).map(entry => path.join(directory, entry.name))) : [];


const topicBody = (componentName: string): string => [
    'mcs.metadata:',
    `  componentName: ${componentName}`,
    'kind: AdaptiveDialog',
    'beginDialog:',
    '  kind: OnRecognizedIntent',
    '  id: main',
    '  intent:',
    `    displayName: ${componentName}`,
    '    triggerQueries:',
    `      - ${componentName}`,
    '',
    '  actions:',
    '    - kind: SendActivity',
    '      id: sendMessage_new',
    `      activity: ${componentName} is live.`,
    '',
].join('\n');

export const newComponentSourceRoot = (fixturesRoot: string, shape: ShapeDescriptor): string =>
    path.join(fixturesRoot, 'new-components', shape.id);

export const addNewComponents = (agentRoot: string, shape: ShapeDescriptor, fixturesRoot: string, agentSchemaName: string, suffix: string): ComponentFact[] => {
    const source = newComponentSourceRoot(fixturesRoot, shape);
    const originalSchemaName = readSchemaName(path.join(fixturesRoot, shape.fixtureDirectory));
    const facts: ComponentFact[] = [];

    if (fs.existsSync(source)) {
        for (const entry of fs.readdirSync(source, { withFileTypes: true })) {
            copyDirectory(path.join(source, entry.name), path.join(agentRoot, entry.name));
        }

        applyReplacements(agentRoot, new Map([[originalSchemaName, agentSchemaName]]));
    }

    const knowledgeName = `TestFile2${suffix}.txt`;
    const knowledgePath = path.join(agentRoot, ...shape.knowledgeRelativeDir.split('/'), knowledgeName);
    fs.mkdirSync(path.dirname(knowledgePath), { recursive: true });
    fs.writeFileSync(knowledgePath, `knowledge added by the end to end suite, run ${suffix}\n`, 'utf8');
    facts.push({
        kind: 'knowledge',
        relativePath: relativePosix(agentRoot, knowledgePath),
        cloudName: knowledgeName,
        expectMaterialisedMetadata: `${knowledgeName}.mcs.yml`,
    });

    if (shape.supportsTopics) {
        const componentName = `NewTopic ${suffix}`;
        const topicPath = path.join(agentRoot, 'topics', `NewTopic${suffix}.mcs.yml`);
        fs.mkdirSync(path.dirname(topicPath), { recursive: true });
        fs.writeFileSync(topicPath, topicBody(componentName), 'utf8');
        facts.push({ kind: 'topic', relativePath: relativePosix(agentRoot, topicPath), cloudName: componentName });
    }

    const skillRoot = path.join(agentRoot, ...shape.bareSkillRelativeDir.split('/'));

    if (shape.bareSkillRelativeDir && fs.existsSync(skillRoot)) {
        facts.push({
            kind: 'skill',
            relativePath: shape.bareSkillRelativeDir,
            cloudName: path.basename(shape.bareSkillRelativeDir),
            expectMaterialisedMetadata: 'skill.mcs.yml',
        });
    }

    return facts;
};

export const survivingComponents = (agentRoot: string, facts: readonly ComponentFact[]): { missing: string[] } => ({
    missing: facts
        .filter(fact => !fs.existsSync(path.join(agentRoot, ...fact.relativePath.split('/'))))
        .map(fact => `${fact.kind}:${fact.relativePath}`),
});

export const declaredConnectionReferences = (agentRoot: string): string[] => {
    const sources = [path.join(agentRoot, 'connectionreferences.mcs.yml'), ...listFilesIn(path.join(agentRoot, 'infrastructure', 'connections'))];
    const found: string[] = [];

    for (const source of sources.filter(candidate => fs.existsSync(candidate))) {
        for (const line of fs.readFileSync(source, 'utf8').split('\n')) {
            const match = /connectionReferenceLogicalName:\s*(\S+)/.exec(line);

            if (match) {
                found.push(match[1]);
            }
        }
    }

    return found;
};

export const runMintedConnectionReferences = (agentRoot: string, agentSchemaName: string): string[] =>
    [...new Set(declaredConnectionReferences(agentRoot).filter(name => name.startsWith(`${agentSchemaName}.`)))];

export const requiredConnectionReferences = (agentRoot: string): string[] => {
    const required: string[] = [];

    for (const metadata of listFilesIn(path.join(agentRoot, 'workflows'), true).filter(candidate => path.basename(candidate) === 'metadata.yml')) {
        let collecting = false;

        for (const line of fs.readFileSync(metadata, 'utf8').split('\n')) {
            if (/^connectionReferences:\s*$/.test(line.trim()) || /^connectionReferences:\s*$/.test(line)) {
                collecting = true;
                continue;
            }

            if (collecting) {
                const match = /^\s*-\s*(\S+)\s*$/.exec(line);

                if (!match) {
                    collecting = false;
                    continue;
                }

                required.push(match[1]);
            }
        }
    }

    return required;
};

export const metadataMaterialised = (agentRoot: string, fact: ComponentFact): boolean => {
    if (!fact.expectMaterialisedMetadata) {
        return true;
    }

    const directory = fact.kind === 'skill'
        ? path.join(agentRoot, ...fact.relativePath.split('/'))
        : path.dirname(path.join(agentRoot, ...fact.relativePath.split('/')));

    return fs.existsSync(path.join(directory, fact.expectMaterialisedMetadata));
};

export interface RemovedComponent {
    kind: string;
    relativePath: string;
    cloudName: string;
}

export const removeComponent = (agentRoot: string, relativePath: string, kind: string, cloudName: string): RemovedComponent => {
    const target = path.join(agentRoot, ...relativePath.split('/'));

    if (!fs.existsSync(target)) {
        throw new Error(`cannot delete ${relativePath}: it is not in ${agentRoot}`);
    }

    fs.rmSync(target, { recursive: true, force: true });
    return { kind, relativePath, cloudName };
};
