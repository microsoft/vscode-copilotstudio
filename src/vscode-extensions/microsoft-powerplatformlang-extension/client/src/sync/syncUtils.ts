import * as path from 'path';
import { existsSync } from 'fs';

// Knowledge-file content folder is shape-keyed (TDD D34), NOT a migration: classic agents
// keep knowledge/files/; CLI layered agents use capabilities/knowledge/files/, mirroring the
// C# sync engine and the projection. The CLI layered layout is signalled by the agent.sync.yaml
// marker the CLI clone emits (classic clones never emit it, see the C# WorkspaceLayout marker).
// Exported as the canonical client-side CLI-layout signal (TDD D29/D36): clone post-open and other
// client paths reuse it instead of re-deriving the marker (the TS CloneAgentResponse does not carry
// AuthoringShape, so the on-disk marker is the threaded signal, D26).
export function isCliLayeredWorkspace(root: string): boolean {
  return existsSync(path.join(root, 'agent.sync.yaml'));
}
