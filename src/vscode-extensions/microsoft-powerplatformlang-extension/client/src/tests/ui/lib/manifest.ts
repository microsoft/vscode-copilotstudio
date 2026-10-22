import type { ShapeId } from './shapes';

export type Outcome = 'PASS' | 'FAIL' | 'BLOCKED' | 'NOT-RUN';

export const formatSeconds = (milliseconds: number): string =>
    milliseconds >= 60_000
        ? `${Math.floor(milliseconds / 60_000)}m${String(Math.round((milliseconds % 60_000) / 1000)).padStart(2, '0')}s`
        : `${(milliseconds / 1000).toFixed(1)}s`;

export const ellipsise = (value: string, width: number): string =>
    value.length <= width ? value : `${value.slice(0, width - 1)}…`;

export const scenarioLabel = (id: string, shape: string): string => `${id} [${shape}]`;

export const scenarioLine = (id: string, shape: string, name: string, outcome: Outcome, milliseconds: number): string =>
    `  ${scenarioLabel(id, shape).padEnd(16)} ${ellipsise(name, 46).padEnd(46)} ${outcome.padEnd(8)}${formatSeconds(milliseconds).padStart(7)}`;

export const splitExpectation = (detail: string): { what: string; expected?: string; actual?: string } => {
    const match = /^([\s\S]*?): expected ([\s\S]*), actual ([\s\S]*)$/.exec(detail);

    return match ? { what: match[1], expected: match[2], actual: match[3] } : { what: detail };
};


export interface DeclaredScenario {
    id: string;
    name: string;
    shape: ShapeId;
}

export interface ScenarioResult {
    id: string;
    name: string;
    shape: ShapeId;
    outcome: Outcome;
    milliseconds: number;
    detail?: string;
}

export class RunManifest {
    private readonly declared: DeclaredScenario[] = [];
    private readonly results = new Map<string, ScenarioResult>();

    private static key(id: string, shape: ShapeId): string {
        return `${id}:${shape}`;
    }

    declare(scenario: DeclaredScenario): void {
        this.declared.push(scenario);
    }

    record(result: ScenarioResult): void {
        this.results.set(RunManifest.key(result.id, result.shape), result);
    }

    reconcile(): ScenarioResult[] {
        return this.declared.map(scenario => this.results.get(RunManifest.key(scenario.id, scenario.shape)) ?? {
            ...scenario,
            outcome: 'NOT-RUN' as Outcome,
            milliseconds: 0,
        });
    }

    counts(): Record<Outcome, number> {
        const totals: Record<Outcome, number> = { 'PASS': 0, 'FAIL': 0, 'BLOCKED': 0, 'NOT-RUN': 0 };

        for (const result of this.reconcile()) {
            totals[result.outcome]++;
        }

        return totals;
    }

    failed(): boolean {
        const totals = this.counts();
        return totals.FAIL > 0 || totals['NOT-RUN'] > 0 || totals.BLOCKED > 0;
    }
}

