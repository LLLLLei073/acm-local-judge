import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ProblemDraftRepository } from '../src/domain/problem-drafts.ts';
class Store {
  data=new Map();
  get length(){return this.data.size;}
  key(index){return [...this.data.keys()][index]??null;}
  getItem(key){return this.data.get(key)??null;}
  setItem(key,value){this.data.set(key,value);}
  removeItem(key){this.data.delete(key);}
}
const problem=id=>({id,title:'题目 '+id,statement:'说明',tests:[],limits:{cpuMs:2000}});
const entry=id=>({id:'problem:'+id,problem:problem(id),baseline:'original',updatedAt:'2026-10-20T01:00:00Z'});
test('saving or discarding one problem preserves independent drafts and their contents',()=>{
  const storage=new Store(),repository=new ProblemDraftRepository(storage);
  const a=entry('a'),b=entry('b'); a.problem.statement='A 的未保存内容'; b.problem.statement='B 的未保存内容';
  assert(repository.save(a));assert(repository.save(b));
  a.problem.statement='later mutation'; assert.equal(repository.read('problem:a').problem.statement,'A 的未保存内容');
  assert(repository.remove('problem:a'));assert.equal(repository.read('problem:a'),undefined);
  assert.equal(repository.read('problem:b').problem.statement,'B 的未保存内容');
});
test('legacy drafts are available without silently deleting the original recovery data',()=>{
  const storage=new Store();storage.setItem('acm:admin:problem-draft',JSON.stringify({problem:problem('a'),updatedAt:'2026-01-01'}));
  const repository=new ProblemDraftRepository(storage);assert.equal(repository.list()[0].id,'problem:a');
  assert(repository.save({...entry('a'),problem:{...problem('a'),statement:'新版草稿'}}));
  assert.equal(repository.list().length,1);assert.equal(repository.read('problem:a').problem.statement,'新版草稿');
  assert(storage.getItem('acm:admin:problem-draft'));repository.remove('problem:a');assert.equal(repository.list().length,0);
});
test('a damaged entry or legacy JSON does not hide healthy drafts',()=>{
  const storage=new Store(),repository=new ProblemDraftRepository(storage);repository.save(entry('b'));
  storage.setItem('acm:admin:draft:v2:broken','{');storage.setItem('acm:admin:problem-draft','invalid');
  assert.equal(repository.list().length,1);assert(repository.remove('problem:b'));assert.equal(repository.list().length,0);
  assert.equal(storage.getItem('acm:admin:draft:v2:broken'),'{');
});
test('storage quota failures are explicit and preserve the last recovery point',()=>{
  const storage=new Store(),repository=new ProblemDraftRepository(storage);repository.save(entry('a'));
  storage.setItem=()=>{throw new Error('quota');};
  assert.equal(repository.save({...entry('a'),problem:{...problem('a'),statement:'new'}}),false);
  assert.equal(repository.read('problem:a').problem.statement,'说明');
});
