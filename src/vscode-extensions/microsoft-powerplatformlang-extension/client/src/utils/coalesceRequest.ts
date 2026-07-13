export function coalesceRequest<T>(
    inFlight: Map<string, Promise<T>>,
    key: string,
    start: () => Promise<T>,
    callerSignal?: AbortSignal | null
): Promise<T> {
    let shared = inFlight.get(key);
    if (shared === undefined) {
        shared = (async () => {
            try {
                return await start();
            } finally {
                inFlight.delete(key);
            }
        })();
        inFlight.set(key, shared);
    }

    if (!callerSignal) {
        return shared;
    }

    const sharedRequest = shared;
    return new Promise<T>((resolve, reject) => {
        const rejectAborted = () => reject(new DOMException('The operation was aborted.', 'AbortError'));
        if (callerSignal.aborted) {
            rejectAborted();
            return;
        }
        const onAbort = () => rejectAborted();
        callerSignal.addEventListener('abort', onAbort, { once: true });
        sharedRequest.then(
            value => { callerSignal.removeEventListener('abort', onAbort); resolve(value); },
            error => { callerSignal.removeEventListener('abort', onAbort); reject(error); }
        );
    });
}
