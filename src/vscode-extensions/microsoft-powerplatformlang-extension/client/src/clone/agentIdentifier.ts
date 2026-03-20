import { CoreServicesClusterCategory, DefaultCoreServicesClusterCategory } from "../constants";
import { AgentIdentifier} from "../types";

const portalUriRegex = new RegExp(
    /(?:https:\/\/)?(?:copilotstudio\.)(?<realm>\w+)?\.?microsoft\.com\/environments\/(?<environmentId>[\w-]+)(?:\/bots\/)?(?<botId>[\w-]+)?/i
);

const realmToClusterCategory: Record<string, CoreServicesClusterCategory> = {
    "preview": CoreServicesClusterCategory.FirstRelease,
    "preprod": CoreServicesClusterCategory.Preprod,
    "test": CoreServicesClusterCategory.Test,
};

export function tryGetAgentIdentifier(portalUri: string): AgentIdentifier | null {
    const trimmed = portalUri.trim();
    const match = portalUriRegex.exec(trimmed);
    if (match) {
        // Access groups by index based on the regex capture order
        const realm = match[1]; // First capture group is the realm
        let clusterCategory = realmToClusterCategory[realm] || DefaultCoreServicesClusterCategory;

        const environmentId = match[2]; // Second capture group is environmentId
        let agentId: string | undefined = match[3] || undefined; // Third capture group is botId

        return {
            environmentId,
            agentId,
            clusterCategory
        };
    } else {
        return null;
    }
}
