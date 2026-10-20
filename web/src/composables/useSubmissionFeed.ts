import { onBeforeUnmount, ref, type Ref } from 'vue';
import { api } from '../client';
import { followSubmission, type ConnectionState, type SubmissionUpdate } from '../services/submission-stream';
import type { Result, Submission, SubmissionContext } from '../types';

export function useSubmissionFeed(submission: Ref<Submission | undefined>, result: Ref<Result | undefined>, complete: () => void, context?: Ref<SubmissionContext | undefined>) {
  const connection = ref<ConnectionState>('done');
  let dispose: (() => void) | undefined;
  const close = () => { dispose?.(); dispose = undefined; connection.value = 'done'; };
  const connect = (id: string) => {
    close(); connection.value = 'recovering';
    dispose = followSubmission(id, {
      open: url => new EventSource(url), read: id => api<SubmissionUpdate>('/submissions/' + id),
      update: data => { submission.value = data.submission; result.value = data.result; if(context) context.value=data.context??undefined; },
      state: state => connection.value = state, complete,
    });
  };
  onBeforeUnmount(close);
  return { connect, close, connection };
}
