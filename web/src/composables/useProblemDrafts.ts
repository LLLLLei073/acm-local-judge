import { computed, ref, watch, type Ref } from 'vue';
import { ProblemDraftRepository } from '../domain/problem-drafts';
import type { Problem } from '../types';

export function useProblemDrafts(draft:Ref<Problem|undefined>) {
  const repository=new ProblemDraftRepository(localStorage);
  const entries=ref(repository.list()), activeId=ref(''), baseline=ref(''), persisted=ref(true), status=ref('');
  const dirty=computed(()=>!!draft.value&&JSON.stringify(draft.value)!==baseline.value);
  const refresh=()=>entries.value=repository.list();
  function open(problem:Problem) {
    activeId.value=problem.id?'problem:'+problem.id:'new:'+crypto.randomUUID();
    const saved=repository.read(activeId.value);
    baseline.value=saved?.baseline||JSON.stringify(problem);
    draft.value=JSON.parse(JSON.stringify(saved?.problem??problem));
    persisted.value=true;
    status.value=saved?'已恢复本题未保存草稿；请检查后保存':'编辑将按题目分别保存在本浏览器';
    return !!saved;
  }
  function restore(id:string) {
    const saved=repository.read(id); if(!saved)return false;
    activeId.value=id; baseline.value=saved.baseline; draft.value=JSON.parse(JSON.stringify(saved.problem));
    status.value='已恢复本机草稿；请检查后保存'; persisted.value=true; return true;
  }
  function remove(id:string) {
    if(!repository.remove(id)) { status.value='无法清除本机草稿'; return false; }
    refresh(); return true;
  }
  function saved() { const id=activeId.value; draft.value=undefined; if(id)remove(id); activeId.value=''; }
  watch(draft, value=>{
    if(!value||!activeId.value||!dirty.value)return;
    persisted.value=repository.save({id:activeId.value,problem:value,baseline:baseline.value,updatedAt:new Date().toISOString()});
    status.value=persisted.value?'本题草稿已保存在本浏览器':'本机存储不足，请先保存到题库或备份源码';
    refresh();
  },{deep:true});
  return {entries,open,restore,remove,saved,dirty,persisted,status,activeId};
}
