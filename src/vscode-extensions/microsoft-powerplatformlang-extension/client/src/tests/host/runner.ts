import { run as runNodeTests } from 'node:test';
import * as fs from 'node:fs';
import * as path from 'node:path';

export async function run(): Promise<void> {
  const testDir = __dirname;
  const files = fs
    .readdirSync(testDir, { recursive: true })
    .filter((name): name is string => typeof name === 'string' && name.endsWith('.test.js'))
    .map((name) => path.join(testDir, name));

  if (files.length === 0) {
    throw new Error(`No test files matching *.test.js found in ${testDir}`);
  }

  const stream = runNodeTests({ files, isolation: 'none' });

  let passed = 0;
  let failed = 0;

  for await (const event of stream) {
    if (event.type === 'test:pass') {
      passed++;
      console.log(`PASS: ${event.data.name}`);
    } else if (event.type === 'test:fail') {
      failed++;
      console.error(`FAIL: ${event.data.name}`);
      const err = event.data.details?.error as Error | undefined;
      if (err) {
        console.error(err.stack ?? err.message ?? err);
      }
    } else if (event.type === 'test:diagnostic') {
      console.log(`# ${event.data.message}`);
    }
  }

  console.log(`\n${passed} passed, ${failed} failed`);

  if (failed > 0) {
    throw new Error(`${failed} test(s) failed`);
  }
}
