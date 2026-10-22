export type ScenarioGroup = 'vscode' | 'workflow' | 'interop';
export type ScenarioSelector = ScenarioGroup | 'smoke';

export const ALL_GROUPS: readonly ScenarioGroup[] = ['vscode', 'workflow', 'interop'];
export const ALL_SELECTORS: readonly ScenarioSelector[] = ['vscode', 'workflow', 'interop', 'smoke'];

export interface CatalogueEntry {
    id: string;
    name: string;
    group: ScenarioGroup;
    smoke?: true;
}

export const CATALOGUE: readonly CatalogueEntry[] = [
    { id: 'A-01', name: 'attach a fixture with no .mcs', group: 'vscode', smoke: true },
    { id: 'B-01', name: 'clone into two working copies', group: 'vscode', smoke: true },
    { id: 'B-02', name: "v1's edit reaches the cloud", group: 'vscode', smoke: true },
    { id: 'B-03', name: 'v2 previews it, writes nothing', group: 'vscode' },
    { id: 'B-04', name: 'v2 pulls and matches v1', group: 'vscode', smoke: true },
    { id: 'B-05', name: 'conflict blocks push until it is resolved', group: 'vscode' },
    { id: 'B-06', name: 'pending change visible as a diff', group: 'vscode' },
    { id: 'B-07', name: 'new components reach the cloud', group: 'vscode' },
    { id: 'B-08', name: 'deleted components leave both sides', group: 'vscode' },
    { id: 'D-01', name: 'remote knowledge tree lists cloud files', group: 'vscode' },
    { id: 'D-02', name: 'download all writes knowledge to disk', group: 'vscode' },
    { id: 'D-03', name: 'refreshed agents tree resolves the agent', group: 'vscode' },
    { id: 'D-04', name: 'account selection keeps the signed-in account', group: 'vscode' },
    { id: 'D-05', name: 'manage connections opens for a connected agent', group: 'vscode' },
    { id: 'E-01', name: 'reattach of an attached workspace keeps the agent', group: 'vscode' },
    { id: 'E-02', name: 'invalid yaml blocks apply', group: 'vscode' },
    { id: 'E-03', name: 'a second get writes nothing', group: 'vscode' },
    { id: 'E-04', name: 'preview with nothing pending reports completion', group: 'vscode' },
    { id: 'F-01', name: 'an edited component appears in the local change list', group: 'vscode' },
    { id: 'F-02', name: 'a pushed component appears in the remote change list', group: 'vscode' },
    { id: 'W-01', name: 'reattach binds a standalone workflow to the cloud', group: 'workflow' },
    { id: 'W-02', name: 'clone the cloud flow into two working copies', group: 'workflow' },
    { id: 'W-03', name: "v1's workflow edit reaches the cloud", group: 'workflow' },
    { id: 'W-04', name: 'v2 pulls the workflow and matches v1', group: 'workflow' },
    { id: 'C-01', name: 'cloned by pac, pulled and pushed by vscode', group: 'interop' },
    { id: 'C-02', name: 'cloned by msagent, pulled and pushed by vscode', group: 'interop' },
    { id: 'C-03', name: 'cloned by vscode, pulled and pushed by msagent', group: 'interop' },
    { id: 'C-04', name: 'cloned by vscode, pulled and pushed by pac', group: 'interop' },
];

export const parseGroups = (value: string | undefined): readonly ScenarioSelector[] => {
    const requested = (value ?? '').split(',').map(entry => entry.trim().toLowerCase()).filter(Boolean);

    if (requested.length === 0 || requested.includes('all')) {
        return ALL_GROUPS;
    }

    const unknown = requested.filter(entry => !ALL_SELECTORS.includes(entry as ScenarioSelector));

    if (unknown.length > 0) {
        throw new Error(`unknown group '${unknown.join(', ')}'; expected ${ALL_SELECTORS.join(', ')} or all`);
    }

    return ALL_SELECTORS.filter(selector => requested.includes(selector));
};

export const catalogueEntry = (id: string): CatalogueEntry | undefined => CATALOGUE.find(entry => entry.id === id);

const scenarioPrefixOf = (file: string): string => {
    const name = file.replace(/\\/g, '/').split('/').pop() ?? file;
    return (name.replace(/^group/i, '').charAt(0) ?? '').toUpperCase();
};

const groupRankOf = (file: string): number => {
    const entry = CATALOGUE.find(candidate => candidate.id.startsWith(`${scenarioPrefixOf(file)}-`));
    const rank = entry ? ALL_GROUPS.indexOf(entry.group) : -1;

    return rank < 0 ? ALL_GROUPS.length : rank;
};

export const orderScenarioFiles = (files: readonly string[]): string[] =>
    [...files].sort((left, right) => groupRankOf(left) - groupRankOf(right) || left.localeCompare(right));

export const isSelected = (id: string, shapeScenarios: readonly string[], selected: readonly ScenarioSelector[]): boolean => {
    const entry = catalogueEntry(id);

    if (entry === undefined || !shapeScenarios.includes(id)) {
        return false;
    }

    return (selected.includes('smoke') && entry.smoke === true) || selected.includes(entry.group);
};
