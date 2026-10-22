import * as vscode from 'vscode';
import { stub } from './log';

export type Restore = () => void;

const hasProperty = (item: vscode.QuickPickItem, property: string): boolean =>
    property in (item as unknown as Record<string, unknown>);

export interface QuickPickRule {
    describe: string;
    match: (item: vscode.QuickPickItem) => boolean;
}

export const agentItemRule: QuickPickRule = {
    describe: 'clipboard agent',
    match: item => hasProperty(item, 'agentData'),
};

export const assetItemRule: QuickPickRule = {
    describe: 'asset',
    match: item => hasProperty(item, 'id'),
};

export const accountItemRule: QuickPickRule = {
    describe: 'account',
    match: item => hasProperty(item, 'account'),
};

export const workflowItemRule = (name: string): QuickPickRule => ({
    describe: `workflow ${name}`,
    match: item => (item as unknown as { workflow?: { name?: string } }).workflow?.name === name,
});

export const environmentRule = (environmentId: string): QuickPickRule => ({
    describe: `environment ${environmentId}`,
    match: item => {
        const environment = (item as unknown as { environment?: { environmentId?: string } }).environment;
        return environment?.environmentId?.toLowerCase() === environmentId.toLowerCase();
    },
});

export const installQuickPickAutoSelect = (rules: readonly QuickPickRule[]): Restore => {
    const windowApi = vscode.window as { createQuickPick: unknown };
    const original = windowApi.createQuickPick as typeof vscode.window.createQuickPick;

    windowApi.createQuickPick = <T extends vscode.QuickPickItem>(): vscode.QuickPick<T> => {
        const picker = original<T>();
        const selectionHandlers: ((items: readonly T[]) => unknown)[] = [];
        const acceptHandlers: (() => unknown)[] = [];
        let answered = false;

        const answer = (): void => {
            if (answered) {
                return;
            }

            for (const rule of rules) {
                const item = picker.items.find(candidate => rule.match(candidate));

                if (!item) {
                    continue;
                }

                answered = true;
                stub('quickPick', picker.title ?? '(untitled)', `${item.label} [${rule.describe}]`);

                setTimeout(() => {
                    (picker as { selectedItems: readonly T[] }).selectedItems = [item];
                    selectionHandlers.forEach(handler => handler([item]));
                    acceptHandlers.forEach(handler => handler());
                }, 0);

                return;
            }
        };

        return new Proxy(picker, {
            get: (target, property) => {
                if (property === 'onDidChangeSelection') {
                    return (handler: (items: readonly T[]) => unknown) => {
                        selectionHandlers.push(handler);
                        return target.onDidChangeSelection(handler);
                    };
                }
                if (property === 'onDidAccept') {
                    return (handler: () => unknown) => {
                        acceptHandlers.push(handler);
                        return target.onDidAccept(handler);
                    };
                }
                const value = Reflect.get(target, property, target);
                return typeof value === 'function' ? value.bind(target) : value;
            },
            set: (target, property, value) => {
                Reflect.set(target, property, value, target);
                if (property === 'items') {
                    answer();
                }
                return true;
            },
        });
    };

    return () => {
        windowApi.createQuickPick = original;
    };
};

export const installShowQuickPickAutoSelect = (rules: readonly QuickPickRule[]): Restore => {
    const windowApi = vscode.window as { showQuickPick: unknown };
    const original = windowApi.showQuickPick as typeof vscode.window.showQuickPick;

    windowApi.showQuickPick = async (items: unknown, options?: { title?: string; placeHolder?: string }): Promise<vscode.QuickPickItem | undefined> => {
        const resolved = await Promise.resolve(items as Thenable<vscode.QuickPickItem[]> | vscode.QuickPickItem[]);
        const prompt = options?.title ?? options?.placeHolder ?? '(untitled)';

        for (const rule of rules) {
            const item = resolved.find(candidate => rule.match(candidate));

            if (item) {
                stub('showQuickPick', prompt, `${item.label} [${rule.describe}]`);
                return item;
            }
        }

        stub('showQuickPick', prompt, `(dismissed, ${resolved.length} item(s) offered)`);
        return undefined;
    };

    return () => {
        windowApi.showQuickPick = original;
    };
};

export interface ModalExpectation {
    contains: string;
    answerWith?: string;
}

export interface ModalRecord {
    message: string;
    answer: string | undefined;
}

export interface ModalCapture {
    restore: Restore;
    seen: ModalRecord[];
}

export const installModalStub = (expectations: readonly ModalExpectation[]): ModalCapture => {
    const windowApi = vscode.window as { showWarningMessage: unknown; showInformationMessage: unknown; showErrorMessage: unknown };
    const originalWarning = windowApi.showWarningMessage;
    const originalInformation = windowApi.showInformationMessage;
    const originalError = windowApi.showErrorMessage;
    const seen: ModalRecord[] = [];

    const respond = (kind: string) => async (message: string, ...rest: unknown[]): Promise<string | undefined> => {
        const actions = rest.filter((entry): entry is string => typeof entry === 'string');
        const expectation = expectations.find(candidate => message.includes(candidate.contains));
        const answer = expectation
            ? (expectation.answerWith ?? actions[0])
            : undefined;

        seen.push({ message, answer });
        stub(kind, message.length > 90 ? `${message.slice(0, 90)}…` : message, answer ?? '(dismissed)');
        return answer;
    };

    windowApi.showWarningMessage = respond('modalWarn');
    windowApi.showInformationMessage = respond('modalInfo');
    windowApi.showErrorMessage = respond('modalError');

    return {
        seen,
        restore: () => {
            windowApi.showWarningMessage = originalWarning;
            windowApi.showInformationMessage = originalInformation;
            windowApi.showErrorMessage = originalError;
        },
    };
};

export const installOpenDialogStub = (folder: string): Restore => {
    const windowApi = vscode.window as { showOpenDialog: unknown };
    const original = windowApi.showOpenDialog;

    windowApi.showOpenDialog = async () => {
        stub('openDialog', 'choose a folder', folder);
        return [vscode.Uri.file(folder)];
    };

    return () => {
        windowApi.showOpenDialog = original;
    };
};

export interface CommandGate {
    restore: Restore;
    execute: (command: string, ...args: unknown[]) => Promise<unknown>;
}

export const installOpenFolderSuppression = (): CommandGate => {
    const commandsApi = vscode.commands as { executeCommand: unknown };
    const original = commandsApi.executeCommand as typeof vscode.commands.executeCommand;

    commandsApi.executeCommand = async (command: string, ...args: unknown[]) => {
        if (command === 'vscode.openFolder') {
            stub('command', 'vscode.openFolder', 'suppressed');
            return undefined;
        }

        return (original as (...inner: unknown[]) => Promise<unknown>)(command, ...args);
    };

    return {
        execute: (command: string, ...args: unknown[]) => (original as (...inner: unknown[]) => Promise<unknown>)(command, ...args),
        restore: () => {
            commandsApi.executeCommand = original;
        },
    };
};
