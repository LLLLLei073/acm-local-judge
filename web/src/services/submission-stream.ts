import type { Result, Submission, SubmissionContext } from '../types';
export interface SubmissionUpdate { submission: Submission; result: Result; context?: SubmissionContext | null }
export type ConnectionState = 'live' | 'recovering' | 'polling' | 'done' | 'offline';
export interface StreamOptions {
  open: (url: string) => EventSource;
  read: (id: string) => Promise<SubmissionUpdate>;
  update: (value: SubmissionUpdate) => void;
  state: (state: ConnectionState) => void;
  complete: () => void;
  pollMs?: number;
}
/** One disposable channel per result view. Stale callbacks cannot mutate the next view. */
export function followSubmission(id: string, options: StreamOptions): () => void {
  let disposed = false, finished = false, stream: EventSource | undefined, timer: ReturnType<typeof setTimeout> | undefined;
  const live = (s: string) => ['Queued', 'Compiling', 'Running'].includes(s);
  const stop = () => { disposed = true; stream?.close(); if (timer) clearTimeout(timer); };
  const accept = (value: SubmissionUpdate) => {
    if (disposed || value.submission.id !== id) return;
    options.update(value);
    if (!live(value.submission.status)) { finished = true; options.state('done'); stop(); options.complete(); }
  };
  const poll = async () => {
    if (disposed) return;
    try { const value = await options.read(id); if (disposed) return; options.state('polling'); accept(value); }
    catch { if (!disposed) options.state('offline'); }
    if (!disposed && !finished) timer = setTimeout(poll, options.pollMs ?? 1800);
  };
  const recover = () => {
    if (disposed) return;
    stream?.close(); options.state('recovering');
    if (!timer) timer = setTimeout(poll, 0);
  };
  try {
    stream = options.open('/api/submissions/' + encodeURIComponent(id) + '/events');
    stream.onopen = () => { if (!disposed) options.state('live'); };
    stream.onmessage = event => { try { accept(JSON.parse(event.data)); } catch { recover(); } };
    stream.onerror = recover;
  } catch { recover(); }
  return stop;
}
