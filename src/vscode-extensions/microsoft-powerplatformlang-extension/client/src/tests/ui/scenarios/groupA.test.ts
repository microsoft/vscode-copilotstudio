import { describe, test } from 'node:test';
import { SHAPES } from '../lib/shapes';
import { runScenario, declareAll, dataverseToken, settings, normaliseId } from '../lib/run';
import { listBotComponents } from '../lib/dataverse';
import { assertThat, detail } from '../lib/log';
import { fileSet } from '../lib/tree';
import * as path from 'node:path';
import * as fs from 'node:fs';

declareAll();

describe('Group A — attach three fixtures to the cloud', () => {
    for (const shape of SHAPES) {
        test(`A-01 [${shape.id}] attach a fixture with no .mcs`, { timeout: 1_200_000 }, async () => {
            await runScenario('A-01', 'attach a fixture with no .mcs', shape, async seed => {
                assertThat('agent id returned', 'a guid', seed.agentId, /^[0-9a-f-]{36}$/i.test(seed.agentId));
                assertThat('bound environment', settings.environmentId, seed.connection.EnvironmentId,
                    normaliseId(seed.connection.EnvironmentId) === normaliseId(settings.environmentId));

                const marker = path.join(seed.seedPath, shape.markerFile);
                assertThat('shape marker present', shape.markerFile, fs.existsSync(marker) ? 'present' : 'absent', fs.existsSync(marker));

                const components = await listBotComponents(seed.connection, await dataverseToken(seed.connection.DataverseEndpoint));
                detail('cloud', `${components.length} components under ${seed.displayName}`);
                assertThat('cloud holds components', '> 0', String(components.length), components.length > 0);
                const local = fileSet(seed.seedPath).filter(relative => relative.endsWith('.mcs.yml') && !relative.endsWith('settings.mcs.yml'));
                assertThat('workspace kept its components', '> 0', String(local.length), local.length > 0);
            }, true);
        });
    }
});
