import { runTestsInDirectory } from '../runnerCore';

export async function run(): Promise<void> {
  await runTestsInDirectory(__dirname);
}
