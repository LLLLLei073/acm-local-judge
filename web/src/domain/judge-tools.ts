import type { Problem } from '../types';
export type JudgeTool='checker'|'interactor'|'manager';
export function judgeTools(p:Pick<Problem,'mode'|'managedCommunication'|'flow'>):{required:JudgeTool[];optional:JudgeTool[];guide:string} {
  if(p.mode==='Special')return {required:['checker'],optional:[],guide:'checker input.txt output.txt answer.txt；退出码 0=AC、1=WA、2=PE，其余退出码或超限为 Judge Error。支持 testlib.h。'};
  if(p.mode==='Interactive')return {required:['interactor'],optional:['checker'],guide:'interactor input.txt transcript.txt answer.txt；通过 stdin/stdout 与选手实时问答。若使用后置 checker，交互器需写出 transcript.txt。'};
  if(p.mode==='Communication'&&p.managedCommunication)return {required:['manager'],optional:[],guide:'manager 通过 acm_manager.hpp 的 JSON Lines 协议依次启动两轮；两轮必须为新进程与新工作目录。'};
  if(p.mode==='Communication')return {required:[],optional:['checker'],guide:'引擎依次执行两轮，转发第一轮原始输出。最终输出默认按 token 精确比较，也可填写 checker。每轮分别限额，资源取最大值。'};
  if(p.mode==='Multiprocess'&&p.flow?.enabled)return {required:[],optional:[],guide:'当前使用可视化通信流程。到「成员与通信」配置顺序、消息来源与校验；已有 manager 源码保留但不执行。'};
  if(p.mode==='Multiprocess')return {required:['manager'],optional:[],guide:'manager 通过 acm_manager.hpp 调度同一程序的多个实例。argv[1] 为实例编号，argv[2] 为总数；每个实例分别限额，最终取最大值。'};
  return {required:[],optional:[],guide:'标准评测由引擎按 ASCII 空白拆分 token，逐个精确比较。无需判题源码，不做浮点容差或数值转换。'};
}
export function bruteAssessment(rows:{verdict:string}[]) {
  const failed=rows.find(row=>row.verdict!=='AC');
  if(!failed)return {tone:'brute-warn',text:'该程序通过了当前全部数据。这只能说明现有数据尚未区分它；是否需要强化取决于程序本身与题目约束。'};
  if(failed.verdict==='WA'||failed.verdict==='PE')return {tone:'brute-ok',text:'已发现答案或格式差异。可据此检查候选程序的逻辑；这不能证明其复杂度不达标。'};
  if(failed.verdict==='TLE'||failed.verdict==='MLE')return {tone:'brute-warn',text:'该程序触及资源限制。请结合 CPU、墙钟和读写协议检查原因；协议等待或未 flush 也可能导致超时。'};
  return {tone:'brute-warn',text:'运行未完成：'+failed.verdict+'。先检查编译、崩溃、输出或判题工具错误，再评价数据强度。'};
}
