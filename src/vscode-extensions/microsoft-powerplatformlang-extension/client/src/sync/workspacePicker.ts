import { window } from "vscode";
import { CopilotStudioWorkspace, getAllWorkspaces, getDuplicateDisplayNames } from "./localWorkspaces";

export function selectWorkspace() : Promise<CopilotStudioWorkspace | undefined> {
    return new Promise((resolve) => {
        const workspaces = getAllWorkspaces();
        if (workspaces.length === 0) {
            resolve(undefined);
            return;
        } else if (workspaces.length === 1) {
            resolve(workspaces[0]);
            return;
        } else {
            const duplicateNames = getDuplicateDisplayNames(workspaces);
            const workspaceItems = workspaces.map(workspace => {
                const isDuplicate = duplicateNames.has(workspace.displayName.toLowerCase());
                const accountEmail = workspace.syncInfo?.accountInfo?.accountEmail;
                const environmentId = workspace.syncInfo?.environmentId;
                const detailParts: string[] = [];
                if (accountEmail) {
                    detailParts.push(`account: ${accountEmail}`);
                }
                if (environmentId) {
                    detailParts.push(`env: ${environmentId}`);
                }
                return {
                    label: workspace.displayName,
                    description: isDuplicate && workspace.schemaName ? workspace.schemaName : workspace.description,
                    detail: detailParts.length > 0 ? detailParts.join(' · ') : undefined,
                    iconPath: workspace.icon,
                    type: workspace.type,
                    info: workspace.syncInfo,
                    data: workspace,
                };
            });
            window.showQuickPick(workspaceItems, { placeHolder: "Select a workspace" }).then(selected => {
                if (selected) {
                    resolve(selected.data);
                } else {
                    resolve(undefined);
                }
            });
        }
    });
}