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

  // Watchdog: force-exit if no test events arrive for QUIET_MS. The node:test
  // event stream is observed not to close after all test results have been
  // emitted -- the implicit root test waits for the event loop to drain,
  // which it can't because host tests activate the extension and register
  // subscriptions (tree views, watchers, file system providers) that are
  // never disposed at end-of-run. Without the watchdog the for-await below
  // blocks indefinitely after the last event.
  const QUIET_MS = 5_000;
  let lastEventAt = Date.now();
  const armed = setInterval(() => {
    if (Date.now() - lastEventAt > QUIET_MS) {
      console.log(`\n${passed} passed, ${failed} failed`);
      console.error(`(watchdog: no test events for ${QUIET_MS}ms - exiting)`);
      process.exit(failed > 0 ? 1 : 0);
    }
  }, 1_000);
  armed.unref?.();

  for await (const event of stream) {
    lastEventAt = Date.now();
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

  clearInterval(armed);
  console.log(`\n${passed} passed, ${failed} failed`);
  process.exit(failed > 0 ? 1 : 0);
}
