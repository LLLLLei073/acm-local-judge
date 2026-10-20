import type { Problem } from '../types';
type LocalStore=Pick<Storage,'getItem'|'setItem'|'removeItem'|'key'|'length'>;
export interface SavedProblemDraft { id:string; problem:Problem; baseline:string; updatedAt:string }
const PREFIX='acm:admin:draft:v2:';
const LEGACY='acm:admin:problem-draft';
const valid=(value:unknown):value is SavedProblemDraft=>{
  const row=value as SavedProblemDraft;
  return !!row&&typeof row.id==='string'&&typeof row.updatedAt==='string'&&typeof row.baseline==='string'&&!!row.problem&&typeof row.problem.title==='string'&&Array.isArray(row.problem.tests)&&!!row.problem.limits&&typeof row.problem.statement==='string';
};
export class ProblemDraftRepository {
  private storage:LocalStore;
  constructor(storage:LocalStore) { this.storage=storage; }
  list():SavedProblemDraft[] {
    const result:SavedProblemDraft[]=[];
    try {
      for(let i=0;i<this.storage.length;i++) {
        const key=this.storage.key(i); if(!key?.startsWith(PREFIX))continue;
        try { const row=JSON.parse(this.storage.getItem(key)||'null'); if(valid(row))result.push(row); } catch { /* Keep malformed entries intact so they remain recoverable outside the UI. */ }
      }
      try {
        const old=JSON.parse(this.storage.getItem(LEGACY)||'null');
        if(old?.problem?.limits&&Array.isArray(old.problem.tests)) {
          const id=old.problem.id?'problem:'+old.problem.id:'legacy-new';
          if(!result.some(row=>row.id===id)) result.push({id,problem:old.problem,baseline:'',updatedAt:old.updatedAt||''});
        }
      } catch { /* A broken legacy draft must not hide healthy new drafts. */ }
    } catch { return result; }
    return result.sort((a,b)=>b.updatedAt.localeCompare(a.updatedAt));
  }
  read(id:string) { return this.list().find(row=>row.id===id); }
  save(row:SavedProblemDraft):boolean {
    try { this.storage.setItem(PREFIX+encodeURIComponent(row.id),JSON.stringify(row)); return true; }
    catch { return false; }
  }
  remove(id:string):boolean {
    try {
      this.storage.removeItem(PREFIX+encodeURIComponent(id));
      try {
        const old=JSON.parse(this.storage.getItem(LEGACY)||'null');
        if(old?.problem&&(old.problem.id?'problem:'+old.problem.id:'legacy-new')===id) this.storage.removeItem(LEGACY);
      } catch { /* Keep unreadable legacy content instead of blocking removal of a healthy new draft. */ }
      return true;
    } catch { return false; }
  }
}
