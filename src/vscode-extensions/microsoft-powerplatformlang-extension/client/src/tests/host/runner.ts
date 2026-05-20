import 'node:test';
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

  let passed = 0;
  let failed = 0;
  const wrapTest = <T extends (...args: unknown[]) => unknown>(original: T): T => {
    const wrapped = function (this: unknown, ...args: unknown[]) {
      // Signatures: test(name?, options?, fn?)
      let fnIndex = -1;
      for (let i = args.length - 1; i >= 0; i--) {
        if (typeof args[i] === 'function') {
          fnIndex = i;
          break;
        }
      }
      if (fnIndex >= 0) {
        const userFn = args[fnIndex] as (...inner: unknown[]) => unknown;
        args[fnIndex] = async function (this: unknown, ...innerArgs: unknown[]) {
          try {
            await userFn.apply(this, innerArgs);
            passed++;
          } catch (err) {
            failed++;
            throw err;
          }
        };
      }
      return (original as Function).apply(this, args);
    };
    return wrapped as unknown as T;
  };

  const nodeTestExports = require('node:test') as Record<string, unknown>;
  const patch = (key: string) => {
    const original = nodeTestExports[key];
    if (typeof original !== 'function') {
      return;
    }
    try {
      Object.defineProperty(nodeTestExports, key, {
        value: wrapTest(original as (...args: unknown[]) => unknown),
        writable: true,
        configurable: true,
        enumerable: true,
      });
    } catch {
    }
  };
  patch('test');
  patch('it');
  patch('default');

  for (const file of files) {
    require(file);
  }

  // Watchdog: force-exit once stdout has been quiet for QUIET_MS. The
  // implicit node:test harness runs on beforeExit, but host tests activate
  // the extension and register subscriptions (tree views, watchers, file
  // system providers) that are never disposed, keeping the event loop
  // alive past the implicit harness completion. We snoop on
  // process.stdout.write timestamps to detect when the TAP reporter has
  // stopped emitting output.
  let lastWriteAt = Date.now();
  const originalWrite = process.stdout.write.bind(process.stdout);
  type StdoutWrite = (chunk: unknown, ...rest: unknown[]) => boolean;
  (process.stdout as unknown as { write: StdoutWrite }).write = (
    chunk: unknown,
    ...rest: unknown[]
  ): boolean => {
    lastWriteAt = Date.now();
    return (originalWrite as StdoutWrite)(chunk, ...rest);
  };

  const QUIET_MS = 5_000;
  const START_GRACE_MS = 3_000;
  const startedAt = Date.now();
  await new Promise<void>((resolve) => {
    const armed = setInterval(() => {
      if (Date.now() - startedAt < START_GRACE_MS) {
        return;
      }
      if (Date.now() - lastWriteAt > QUIET_MS) {
        clearInterval(armed);
        resolve();
      }
    }, 500);
    armed.unref?.();
  });

  process.stdout.write = originalWrite;
  console.log(`\n${passed} passed, ${failed} failed`);
  const exitCode = failed > 0 ? 1 : (typeof process.exitCode === 'number' ? process.exitCode : 0);
  process.exit(exitCode);
}