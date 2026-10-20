import { test } from 'node:test';
import assert from 'node:assert/strict';
import { followSubmission } from '../src/services/submission-stream.ts';
const update=(status='Running',id='s')=>({submission:{id,status},result:{status,cases:[]}});
function setup(overrides={}) {
  const events={closed:0,close(){this.closed++;}},states=[],values=[];
  let completed=0;
  const dispose=followSubmission('s',{open:()=>events,read:async()=>update('AC'),update:v=>values.push(v),state:s=>states.push(s),complete:()=>completed++,pollMs:2,...overrides});
  return {events,states,values,dispose,get completed(){return completed;}};
}
test('live feed finalizes once and cleans up the stream',()=>{
  const x=setup(); x.events.onopen(); x.events.onmessage({data:JSON.stringify(update())}); x.events.onmessage({data:JSON.stringify(update('AC'))});
  assert.deepEqual(x.states,['live','done']); assert.equal(x.completed,1); assert.equal(x.events.closed,1);
  x.events.onmessage({data:JSON.stringify(update('WA'))}); assert.equal(x.values.length,2);
});
test('disposed or mismatched result callbacks cannot overwrite the next view',()=>{
  const x=setup(); x.events.onmessage({data:JSON.stringify(update('AC','other'))}); assert.equal(x.values.length,0); x.dispose(); x.events.onmessage({data:JSON.stringify(update())}); assert.equal(x.values.length,0);
});
test('SSE failure falls back to polling and survives temporary HTTP errors',async()=>{
  let calls=0,finish; const done=new Promise(resolve=>finish=resolve);
  const x=setup({read:async()=>{if(++calls===1)throw new Error('offline');return update('AC');},complete:finish});
  try { x.events.onerror(); await done; assert.equal(calls,2); assert(x.states.includes('offline')); assert(x.states.includes('polling')); assert.equal(x.values.at(-1).submission.status,'AC'); }
  finally { x.dispose(); }
});
test('invalid SSE payload also recovers instead of leaving a permanently running result',async()=>{
  let finish; const done=new Promise(resolve=>finish=resolve);const x=setup({complete:finish});
  try { x.events.onmessage({data:'invalid-json'}); await done; assert.equal(x.values.at(-1).submission.status,'AC'); }
  finally { x.dispose(); }
});
test('a response completing after disposal produces no UI mutations',async()=>{
  let resolveRead; const reading=new Promise(resolve=>resolveRead=resolve);let started;const began=new Promise(resolve=>started=resolve);
  const x=setup({read:()=>{started();return reading;}});
  x.events.onerror(); await began; x.dispose(); resolveRead(update('AC')); await Promise.resolve(); assert.equal(x.values.length,0); assert.equal(x.completed,0);
});
