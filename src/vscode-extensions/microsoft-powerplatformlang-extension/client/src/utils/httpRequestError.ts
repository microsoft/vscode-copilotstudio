export class HttpRequestError extends Error {
  readonly userMessage: string;

  constructor(status: number, body: unknown) {
    super(`Request failed with status ${status}: ${JSON.stringify(body ?? {})}`);
    const error = body && typeof body === 'object' && 'error' in body ? body.error : undefined;
    const message = error && typeof error === 'object' && 'message' in error ? error.message : undefined;
    this.userMessage = typeof message === 'string' && message.trim()
      ? message : `Request failed with status ${status}`;
  }
}
