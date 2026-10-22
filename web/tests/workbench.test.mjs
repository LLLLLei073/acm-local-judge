import { test } from 'node:test';
import assert from 'node:assert/strict';
import { solvedIds, latestByProblem, trainingSummary, resourcePercent, readiness, pairTestFiles, draftKey, saveLocal, filterTrace, verdictAdvice, filterLeaderboardRows } from '../src/domain/workbench.ts';
import { flowIssues, newFlow, newStep } from '../src/flow.ts';

const submission=(id,problemId,status,createdAt)=>({id,problemId,status,createdAt});
test('leaderboard search never matches hidden identities or pins an unrelated self row',()=>{
  const rows=[{userId:'self',anonymous:true,name:'Secret Name',studentId:'20260001',cfHandle:'HiddenCf'},{userId:'peer',anonymous:false,name:'Visible Peer',studentId:'20260002',cfHandle:'PublicCf'}];
  for(const term of ['Secret','20260001','HiddenCf','missing']) assert.deepEqual(filterLeaderboardRows(rows,term,'self'),[]);
  assert.deepEqual(filterLeaderboardRows(rows,'我','self'),[rows[0]]);
  assert.deepEqual(filterLeaderboardRows(rows,'  publiccf  ','self'),[rows[1]]);
  assert.deepEqual(filterLeaderboardRows(rows,'   ','self'),rows);
});
const p={id:'p',title:'Sum',statement:'Input two integers and output their sum.',rating:800,mode:'Standard',tests:[{name:'01',input:'1 2\n',answer:'3\n'}],exampleSource:'int main(){}',limits:{cpuMs:2000,wallMs:10000,memoryMiB:256,outputKiB:1024},judgeLimits:{cpuMs:10000,wallMs:20000,memoryMiB:512,outputKiB:1024}};
test('AC is retained after a later failure and histories need not arrive sorted',()=>{
  const rows=[submission('1','p','AC','2026-10-18'),submission('2','p','WA','2026-10-19'),submission('3','q','WA','2026-10-20')];
  assert.deepEqual([...solvedIds(rows)],['p']); assert.equal(latestByProblem(rows).get('p').id,'2');
  assert.deepEqual(trainingSummary([{id:'p'},{id:'q'},{id:'hidden'}],rows),{solved:1,attempted:1,running:0});
});
test('stats ignore unavailable problems and resource bars handle invalid limits',()=>{
  assert.deepEqual(trainingSummary([{id:'p'}],[submission('x','hidden','AC',''),submission('y','p','Running','')]),{solved:0,attempted:1,running:1});
  assert.equal(resourcePercent(100,0),0); assert.equal(resourcePercent(200,100),100); assert.equal(resourcePercent(-2,100),0); assert.equal(resourcePercent(NaN,100),0);
});
test('upload pairs case-insensitively, supports valid empty answers, and replaces atomically',()=>{
  const previous=[{name:'01',input:'old',answer:'old'}];
  const output=pairTestFiles([{name:'01.IN',text:'new'},{name:'01.ANS',text:''},{name:'02.in',text:'2'},{name:'02.ans',text:'4'}],'Standard',previous);
  assert.equal(output.added,1); assert.equal(output.replaced,1); assert.equal(output.tests[0].answer,''); assert.equal(previous[0].input,'old');
});
test('missing and duplicate test files reject without damaging existing data',()=>{
  for(const files of [[{name:'01.in',text:''}],[{name:'01.ans',text:''}],[{name:'a.in',text:''},{name:'A.IN',text:''}],[{name:'bad.txt',text:''}]]) assert.throws(()=>pairTestFiles(files,'Standard',p.tests));
  assert.equal(p.tests[0].answer,'3\n'); assert.equal(pairTestFiles([{name:'01.in',text:''}],'Interactive').tests[0].answer,'');
});
test('preparation checks distinguish tools, limits, and solution from proof of a run',()=>{
  assert(readiness(p).every(c=>c.ok));
  assert.equal(readiness({...p,mode:'Special',checker:''}).find(c=>c.id==='tools').ok,false);
  assert.equal(readiness({...p,limits:{...p.limits,cpuMs:0}}).find(c=>c.id==='limits').ok,false);
  assert.equal(readiness({...p,tests:[p.tests[0],p.tests[0]]}).find(c=>c.id==='tests').ok,false);
  assert.equal(readiness({...p,rating:0}).find(c=>c.id==='rating').ok,false);
});
test('draft identity never collides across accounts or encoded problem IDs',()=>{
  assert.notEqual(draftKey('a','p'),draftKey('b','p')); assert.notEqual(draftKey('a:b','c'),draftKey('a','b:c'));
  assert.equal(saveLocal({setItem(){throw new Error('full');}},'key','code'),false);
});
test('trace filters match either side of routing and decode payloads',()=>{
  const trace=[{ms:1,from:'0',to:'manager',dataBase64:btoa('hello')},{ms:2,from:'manager',to:'1',dataBase64:btoa('world')}];
  assert.equal(filterTrace(trace,'manager','world').length,1); assert.equal(filterTrace(trace,'0','').length,1); assert.equal(filterTrace(trace,'1','hello').length,0);
  assert.match(verdictAdvice('TLE'),/flush/); assert.match(verdictAdvice('Judge Error'),/管理员/);
});
test('flow preview detects closed destinations, missing checks and forward references',()=>{
  const flow=newFlow(2); flow.enabled=true;
  assert(flowIssues(flow).some(x=>x.includes('输出校验')));
  const close=newStep('close'),send=newStep('send'),receive=newStep('receive');
  send.data.source='message'; send.data.stepId=receive.id;
  flow.steps=[close,send,receive];
  assert(flowIssues(flow).some(x=>x.includes('已经关闭'))); assert(flowIssues(flow).some(x=>x.includes('尚未接收')));
  flow.steps=[receive]; assert.deepEqual(flowIssues(flow),[]);
});
