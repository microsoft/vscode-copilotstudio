import 'node:test';
import * as fs from 'node:fs';
import * as path from 'node:path';

let activeCancellation: AbortSignal | undefined;
let activeController: AbortController | undefined;
let abandoned: string | undefined;

export const cancellationSignal = (): AbortSignal | undefined => activeCancellation;

export const runWasAbandoned = (): string | undefined => abandoned;

export const abandonRun = (reason: string): void => {
  abandoned ??= reason;
  activeController?.abort();
};

export const runVerdict = (outcome: { passed: number; failed: number; abandoned?: string; reportFailed: boolean }): number =>
    outcome.failed > 0 || outcome.abandoned !== undefined || outcome.reportFailed || outcome.passed + outcome.failed === 0 ? 1 : 0;

const isThenable = (value: unknown): value is Promise<unknown> =>
    typeof (value as { then?: unknown })?.then === 'function';

/**
 * Discovers and runs every compiled test file in a directory, waits for node:test to report
 * every registered test, then force-exits because host tests leave the event loop alive.
 */
export interface RunReporter {
  log: (message: string) => void;
  error: (message: string) => void;
}

export async function runTestsInDirectory(testDir: string, onComplete?: () => void, exitProcess = true, report: RunReporter = console, orderFiles?: (files: readonly string[]) => string[]): Promise<number> {
  const discovered = fs
    .readdirSync(testDir, { recursive: true })
    .filter((name): name is string => typeof name === 'string' && name.endsWith('.test.js'))
    .map((name) => path.join(testDir, name));

  const files = orderFiles ? orderFiles(discovered) : discovered;

  if (files.length === 0) {
    throw new Error(`No test files matching *.test.js found in ${testDir}`);
  }

  let passed = 0;
  let failed = 0;
  let reportFailed = false;
  abandoned = undefined;
  const registered = new Set<Promise<unknown>>();

  const track = (result: unknown): unknown => {
    if (isThenable(result)) {
      const settled = result.then(() => undefined, () => undefined);
      registered.add(settled);
      void settled.then(() => registered.delete(settled));
    }

    return result;
  };

  const wrapSuite = <T extends (...args: unknown[]) => unknown>(original: T): T =>
    (function (this: unknown, ...args: unknown[]) {
      return track((original as Function).apply(this, args));
    }) as unknown as T;

  const wrapHook = <T extends (...args: unknown[]) => unknown>(original: T): T =>
    (function (this: unknown, ...args: unknown[]) {
      const hookIndex = args.findIndex(entry => typeof entry === 'function');

      if (hookIndex >= 0) {
        const userFn = args[hookIndex] as (...inner: unknown[]) => unknown;

        args[hookIndex] = async function (this: unknown, ...innerArgs: unknown[]) {
          const running = Promise.resolve().then(() => userFn.apply(this, innerArgs));
          track(running);

          try {
            return await running;
          } catch (err) {
            failed++;
            report.error(`\n${(err as Error)?.stack ?? String(err)}\n`);
            throw err;
          }
        };
      }

      return (original as Function).apply(this, args);
    }) as unknown as T;

  const wrapTest = <T extends (...args: unknown[]) => unknown>(original: T): T => {
    const wrapped = function (this: unknown, ...args: unknown[]) {
      let fnIndex = -1;
      for (let i = args.length - 1; i >= 0; i--) {
        if (typeof args[i] === 'function') {
          fnIndex = i;
          break;
        }
      }
      if (fnIndex >= 0) {
        const name = typeof args[0] === 'string' ? args[0] : 'a test';
        const userFn = args[fnIndex] as (...inner: unknown[]) => unknown;
        const optionsIndex = args.findIndex(entry =>
          typeof entry === 'object' && entry !== null && typeof (entry as { timeout?: unknown }).timeout === 'number');
        const budget = optionsIndex >= 0 ? (args[optionsIndex] as { timeout: number }).timeout : undefined;

        if (optionsIndex >= 0) {
          args[optionsIndex] = Object.fromEntries(
            Object.entries(args[optionsIndex] as Record<string, unknown>).filter(([key]) => key !== 'timeout'));
        }

        args[fnIndex] = async function (this: unknown, ...innerArgs: unknown[]) {
          if (abandoned) {
            failed++;
            throw new Error(`not started: '${abandoned}' timed out and its work was abandoned, so running anything else would race it`);
          }

          const controller = new AbortController();
          const previousSignal = activeCancellation;
          const previousController = activeController;
          activeCancellation = controller.signal;
          activeController = controller;

          let timer: NodeJS.Timeout | undefined;

          try {
            const started = Promise.resolve().then(() => userFn.apply(this, innerArgs));

            await (budget === undefined ? started : Promise.race([
              started,
              new Promise<never>((_, reject) => {
                timer = setTimeout(() => {
                  abandonRun(name);
                  reject(new Error(`timed out after ${budget}ms and was abandoned while still running`));
                }, budget);
              }),
            ]));

            if (abandoned) {
              throw new Error(`'${abandoned}' was abandoned while this test was running, so its result cannot be trusted`);
            }

            passed++;
          } catch (err) {
            failed++;
            report.error(`\n${(err as Error)?.stack ?? String(err)}\n`);
            throw err;
          } finally {
            if (timer) {
              clearTimeout(timer);
            }

            activeCancellation = previousSignal;
            activeController = previousController;
          }
        };
      }
      return track((original as Function).apply(this, args));
    };
    return wrapped as unknown as T;
  };

  const nodeTestExports = require('node:test') as Record<string, unknown>;
  const patch = (key: string, wrap: <T extends (...args: unknown[]) => unknown>(original: T) => T) => {
    const original = nodeTestExports[key];
    if (typeof original !== 'function') {
      return;
    }
    try {
      Object.defineProperty(nodeTestExports, key, {
        value: wrap(original as (...args: unknown[]) => unknown),
        writable: true,
        configurable: true,
        enumerable: true,
      });
    } catch {
    }
  };
  patch('test', wrapTest);
  patch('it', wrapTest);
  patch('default', wrapTest);
  patch('describe', wrapSuite);
  patch('suite', wrapSuite);
  patch('before', wrapHook);
  patch('after', wrapHook);
  patch('beforeEach', wrapHook);
  patch('afterEach', wrapHook);

  for (const file of files) {
    require(file);
  }

  for (let quiet = 0; quiet < 5;) {
    if (registered.size > 0) {
      await Promise.allSettled([...registered]);
      quiet = 0;
      continue;
    }

    await new Promise(resolve => setTimeout(resolve, 10));
    quiet++;
  }

  report.log(`\n${passed} passed, ${failed} failed`);

  if (abandoned) {
    report.error(`\n'${abandoned}' was abandoned on its timeout; the rest of the run was stopped rather than raced against it\n`);
  }

  if (onComplete) {
    try {
      onComplete();
    } catch (err) {
      report.error(`\nreport generation failed: ${(err as Error)?.stack ?? String(err)}\n`);
      reportFailed = true;
    }
  }

  if (passed + failed === 0) {
    report.error('no test completed, so this run proves nothing');
  }

  const exitCode = runVerdict({ passed, failed, abandoned, reportFailed });

  if (exitProcess) {
    process.exit(exitCode);
  }

  return exitCode;
}
