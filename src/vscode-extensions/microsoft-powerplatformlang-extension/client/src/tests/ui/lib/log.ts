import * as fs from 'node:fs';
import * as path from 'node:path';

export interface LoggedAssertion {
    what: string;
    expected: string;
    actual: string;
    ok: boolean;
}

export interface LoggedStub {
    kind: string;
    prompt: string;
    answer: string;
}

const assertions: LoggedAssertion[] = [];
const stubs: LoggedStub[] = [];

const transcriptPath = process.env.MCSUI_RUN_ROOT ? path.join(process.env.MCSUI_RUN_ROOT, 'console.log') : undefined;
const summaryPath = process.env.MCSUI_RUN_ROOT ? path.join(process.env.MCSUI_RUN_ROOT, 'summary.log') : undefined;

let captured: string[] | undefined;

const append = (target: string, message: string): void => {
    try {
        fs.appendFileSync(target, `${message}\n`, 'utf8');
    } catch {
    }
};

const emit = (message: string): void => {
    if (captured) {
        captured.push(message);
    }

    if (transcriptPath) {
        append(transcriptPath, message);
        return;
    }

    console.log(message);
};

export const show = (message: string): void => {
    emit(message);

    if (summaryPath) {
        append(summaryPath, message);
    }
};

export const beginCapture = (): void => {
    captured = [];
};

export const takeCapture = (): string[] => {
    const lines = captured ?? [];
    captured = undefined;

    return lines;
};

export const line = (message: string): void => emit(`   ${message}`);

export const raw = (message: string): void => emit(message);

export const heading = (message: string): void => emit(`\n── ${message} ${'─'.repeat(Math.max(0, 72 - message.length))}`);

export const detail = (label: string, value: string): void => line(`${label.padEnd(11)} ${value}`);

export const stub = (kind: string, prompt: string, answer: string): void => {
    stubs.push({ kind, prompt, answer });
    line(`[stub] ${kind.padEnd(10)} ${prompt} → ${answer}`);
};

export const check = (what: string, expected: string, actual: string, ok: boolean): void => {
    assertions.push({ what, expected, actual, ok });
    line(`assert      ${what.padEnd(46)} actual ${actual}  ${ok ? '✓' : '✗'}`);
};

export const assertThat = (what: string, expected: string, actual: string, ok: boolean): void => {
    check(what, expected, actual, ok);

    if (!ok) {
        throw new Error(`${what}: expected ${expected}, actual ${actual}`);
    }
};

export const takeAssertions = (): LoggedAssertion[] => assertions.splice(0, assertions.length);

export const takeStubs = (): LoggedStub[] => stubs.splice(0, stubs.length);
