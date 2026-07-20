import { QuickPickItem, QuickPickItemKind, window } from 'vscode';
import { AccountInfo, EnvironmentInfo } from '../types';
import { listStoredAccounts } from '../clients/account';

export type EnvironmentPickItem = QuickPickItem & {
    environment: EnvironmentInfo;
    sourceAccount?: { accountId?: string; accountEmail?: string };
};

export function toEnvironmentPickItem(environment: EnvironmentInfo, sourceAccount?: { accountId?: string; accountEmail?: string }): EnvironmentPickItem {
    return { label: environment.displayName, description: environment.environmentId, environment, sourceAccount };
}

export function buildEnvironmentPickItems(environments: EnvironmentInfo[], sourceAccount?: { accountId?: string; accountEmail?: string }): QuickPickItem[] {
    const seen = new Set<string>();
    const items: QuickPickItem[] = [];
    let lastSku: string | undefined;
    for (const environment of environments) {
        if (seen.has(environment.environmentId)) {
            continue;
        }
        seen.add(environment.environmentId);
        const sku = environment.environmentSku || 'Other';
        if (sku !== lastSku) {
            items.push({ label: sku, kind: QuickPickItemKind.Separator });
            lastSku = sku;
        }
        items.push(toEnvironmentPickItem(environment, sourceAccount));
    }
    return items;
}

export type AccountQuickPickItem = QuickPickItem & { account: AccountInfo };

export interface PickAccountDeps {
    listAccounts: () => Promise<{ accountId: string; accountEmail?: string }[]>;
    showQuickPick: (items: AccountQuickPickItem[], options: { title?: string; placeHolder?: string }) => Thenable<AccountQuickPickItem | undefined>;
}

export async function pickAccount(placeHolder: string = 'Choose an account', deps?: Partial<PickAccountDeps>): Promise<AccountInfo | undefined | 'cancelled'> {
    const listAccounts = deps?.listAccounts ?? listStoredAccounts;
    const showQuickPick = deps?.showQuickPick ?? ((items: AccountQuickPickItem[], options: { title?: string; placeHolder?: string }) => window.showQuickPick(items, options));
    const accounts = (await listAccounts()).map<AccountInfo>(account => ({ accountId: account.accountId, accountEmail: account.accountEmail ?? '', tenantId: '' }));
    if (accounts.length === 0) {
        return undefined;
    }
    if (accounts.length === 1) {
        return accounts[0];
    }
    const items: AccountQuickPickItem[] = accounts.map(account => ({ label: account.accountEmail || account.accountId || 'Account', account }));
    const pick = await showQuickPick(items, { title: 'Select account', placeHolder });
    return pick ? pick.account : 'cancelled';
}
