import { abandonRun } from '../../runnerCore';

export const delay = (milliseconds: number): Promise<void> =>
    new Promise(resolve => setTimeout(resolve, milliseconds));

export const withTimeout = async <T>(operation: Promise<T>, milliseconds: number, description: string): Promise<T> => {
    let timer: NodeJS.Timeout | undefined;

    try {
        return await Promise.race([
            operation,
            new Promise<never>((_, reject) => {
                timer = setTimeout(() => {
                    abandonRun(description);
                    reject(new Error(`${description} did not finish within ${milliseconds}ms and cannot be cancelled, so the run was abandoned rather than raced against it`));
                }, milliseconds);
            }),
        ]);
    } finally {
        if (timer) {
            clearTimeout(timer);
        }
    }
};

export interface PollOptions {
    pollMs?: number;
    describeFailure?: () => string;
}

export const pollUntil = async <T>(attempt: () => Promise<T | undefined>, timeoutMs: number, description: string, options: PollOptions = {}): Promise<T> => {
    const deadline = Date.now() + timeoutMs;

    for (;;) {
        const found = await attempt();

        if (found !== undefined) {
            return found;
        }

        if (Date.now() >= deadline) {
            const extra = options.describeFailure?.();
            throw new Error(`${description} did not happen within ${timeoutMs}ms${extra ? ` — ${extra}` : ''}`);
        }

        await delay(options.pollMs ?? 2_000);
    }
};

export const waitUntil = async (predicate: () => Promise<boolean>, timeoutMs: number, description: string, options: PollOptions = {}): Promise<void> => {
    await pollUntil(async () => (await predicate()) ? true : undefined, timeoutMs, description, options);
};
