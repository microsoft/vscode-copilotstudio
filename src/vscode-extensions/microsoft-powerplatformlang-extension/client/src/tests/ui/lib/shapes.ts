export type ShapeId = 'classic' | 'cli' | 'workflow';
export type ShapeKind = 'agent' | 'workflow';

export interface SeedShape {
    id: ShapeId;
    kind: ShapeKind;
    fixtureDirectory: string;
    markerFile: string;
    openerFile: string;
    identitySubdirectory: string;
    scenarios: readonly string[];
}

export interface ShapeDescriptor extends SeedShape {
    kind: 'agent';
    targetRelativePath: string;
    diffRelativePath: string;
    diffKey: string;
    targetKey: string;
    originalValue: string;
    keyedValue: boolean;
    knowledgeRelativeDir: string;
    bareSkillRelativeDir: string;
    supportsTopics: boolean;
    deletableRelativePath: string;
    deletableCloudName: string;
    deletableKind: string;
    deletableExtraPaths: readonly string[];
    componentKind: string;
    componentName: string;
}

export const SHAPES: readonly ShapeDescriptor[] = [
    {
        id: 'classic',
        kind: 'agent',
        fixtureDirectory: 'classic-agent',
        markerFile: 'agent.mcs.yml',
        openerFile: 'settings.mcs.yml',
        identitySubdirectory: '',
        targetRelativePath: 'topics/Goodbye.mcs.yml',
        diffRelativePath: 'topics/Goodbye.mcs.yml',
        diffKey: 'prompt:',
        targetKey: 'prompt:',
        originalValue: 'Would you like to end our conversation?',
        keyedValue: true,
        knowledgeRelativeDir: 'knowledge/files',
        bareSkillRelativeDir: '',
        supportsTopics: true,
        deletableRelativePath: 'topics/ThankYou.mcs.yml',
        deletableCloudName: 'Thank you',
        deletableKind: 'topic',
        deletableExtraPaths: [],
        componentKind: 'topic',
        componentName: 'Goodbye',
        scenarios: ['A-01', 'B-01', 'B-02', 'B-03', 'B-04', 'B-05', 'B-06', 'B-07', 'B-08', 'D-01', 'D-02', 'D-03', 'D-04', 'D-05', 'E-01', 'E-02', 'E-03', 'E-04', 'F-01', 'F-02', 'C-01', 'C-02', 'C-03', 'C-04'],
    },
    {
        id: 'cli',
        kind: 'agent',
        fixtureDirectory: 'cli-agent',
        markerFile: 'agent.sync.yaml',
        openerFile: 'settings.mcs.yml',
        identitySubdirectory: '',
        targetRelativePath: 'behaviors/skill-1/SKILL.md',
        diffRelativePath: 'behaviors/skill-1/skill.mcs.yml',
        diffKey: 'componentName:',
        targetKey: 'When this skill is activated:',
        originalValue: 'When this skill is activated:',
        keyedValue: false,
        knowledgeRelativeDir: 'capabilities/knowledge/files',
        bareSkillRelativeDir: 'behaviors/get-us-temperature',
        supportsTopics: false,
        deletableRelativePath: 'capabilities/knowledge/files/File3.txt.mcs.yml',
        deletableCloudName: 'File 3.txt',
        deletableKind: 'knowledge',
        deletableExtraPaths: ['capabilities/knowledge/files/File 3.txt'],
        componentKind: 'skill',
        componentName: 'skill-1',
        scenarios: ['A-01', 'B-01', 'B-02', 'B-03', 'B-04', 'B-05', 'B-06', 'B-07', 'B-08', 'D-01', 'D-02', 'D-03', 'D-04', 'D-05', 'E-01', 'E-02', 'E-03', 'E-04', 'F-01', 'F-02', 'C-01', 'C-02', 'C-03', 'C-04'],
    },
];

export const WORKFLOW_SHAPES: readonly SeedShape[] = [
    {
        id: 'workflow',
        kind: 'workflow',
        fixtureDirectory: 'standalone-workflow',
        markerFile: 'workflow.sync.yaml',
        openerFile: 'workflow.sync.yaml',
        identitySubdirectory: '',
        scenarios: ['W-01', 'W-02', 'W-03', 'W-04'],
    },
];

export const ALL_SHAPES: readonly SeedShape[] = [...SHAPES, ...WORKFLOW_SHAPES];

export const variantLine = (shape: ShapeDescriptor, prefix: string): string =>
    shape.keyedValue
        ? `${shape.targetKey} ${prefix} - ${shape.originalValue}`
        : `${prefix} - ${shape.originalValue}`;

export const variantFragment = (shape: ShapeDescriptor, prefix: string): string => `${prefix} - ${shape.originalValue}`;

export const runsScenario = (shape: SeedShape, scenario: string): boolean => shape.scenarios.includes(scenario);
