const HEARTBEAT_MS = 20_000;

export interface SseOptions<T> {
  /** Sent as the first event. */
  initial: T;
  /** Registers a listener that sends an event; returns an unsubscribe function. */
  subscribe: (send: (data: T) => void) => () => void;
  event: string;
  signal: AbortSignal;
}

const noop = () => {};

const formatEvent = (event: string, data: unknown) => `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;

/** A Server-Sent Events response with a heartbeat, cleaned up when the client goes away. */
export function sseResponse<T>({ initial, subscribe, event, signal }: SseOptions<T>): Response {
  const encoder = new TextEncoder();
  let cleanup = noop;
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      const write = (chunk: string) => {
        try {
          controller.enqueue(encoder.encode(chunk));
        } catch {
          cleanup();
        }
      };
      const send = (data: T) => write(formatEvent(event, data));

      write(`retry: 5000\n${formatEvent(event, initial)}`);
      const unsubscribe = subscribe(send);
      const heartbeat = setInterval(() => write(": ping\n\n"), HEARTBEAT_MS);
      cleanup = () => {
        clearInterval(heartbeat);
        unsubscribe();
      };
      signal.addEventListener("abort", () => {
        cleanup();
        try {
          controller.close();
        } catch {}
      });
    },
    cancel() {
      cleanup();
    },
  });
  return new Response(stream, {
    headers: { "content-type": "text/event-stream", "cache-control": "no-store", "x-accel-buffering": "no" },
  });
}
