import type { CommunicationFlow, FlowData, FlowStep, TestData } from './types';

export const flowData = (source: FlowData['source'] = 'literal'): FlowData => ({ source, text: '', stepId: '', startLine: 1, lineCount: 0 });
export const newFlow = (count: number): CommunicationFlow => ({ version: 1, enabled: false, members: Array.from({ length: count }, (_, i) => `角色 ${i}`), steps: [] });
export const newStep = (kind: FlowStep['kind']): FlowStep => ({ id: crypto.randomUUID(), kind, from: 0, to: kind === 'receive' ? -1 : 0, lines: 1, data: flowData('input'), additionalData: [], check: kind === 'receive', expected: flowData('answer'), sample: '' });
export function route(flow: CommunicationFlow, s: FlowStep): string {
  const name = (id: number) => flow.members[id] ?? '已删除成员';
  if (s.kind === 'close') return `调度器 → ${name(s.to)}（关闭输入）`;
  if (s.kind === 'send') return `调度器 → ${name(s.to)}`;
  return `${name(s.from)} → 调度器${s.to >= 0 ? ` → ${name(s.to)}` : ''}`;
}
export function referenceError(steps: FlowStep[]): string {
  const received = new Set<string>();
  for (const [i, s] of steps.entries()) {
    const sources = s.kind === 'send' ? [s.data, ...s.additionalData] : s.kind === 'receive' && s.check ? [s.expected] : [];
    if (sources.some(data => data.source === 'message' && !received.has(data.stepId))) return `步骤 ${i + 1} 引用了尚未接收或已删除的消息，请先修改数据来源。`;
    if (s.kind === 'receive') received.add(s.id);
  }
  return '';
}
export function flowIssues(flow: CommunicationFlow): string[] {
  const issues: string[] = [], closed = new Set<number>(), ids = new Set<string>();
  const reference = referenceError(flow.steps); if (reference) issues.push(reference);
  if (flow.members.length < 2 || flow.members.length > 16 || flow.members.some(n=>!n.trim()) || new Set(flow.members.map(n=>n.trim())).size!==flow.members.length) issues.push('成员需为 2–16 个，名称非空且唯一。');
  const member=(id:number)=>Number.isInteger(id)&&id>=0&&id<flow.members.length;
  for (const [i,s] of flow.steps.entries()) {
    const prefix=`步骤 ${i+1}：`;
    if (!s.id || ids.has(s.id)) issues.push(prefix+'步骤标识缺失或重复。'); ids.add(s.id);
    if (s.kind==='receive') {
      if (!member(s.from)||s.to < -1||s.to>=flow.members.length||s.from===s.to||!Number.isInteger(s.lines)||s.lines<1||s.lines>1000000) issues.push(prefix+'接收成员或读取行数无效。');
      if (closed.has(s.to)) issues.push(prefix+'目标成员输入已经关闭。');
    } else {
      if (!member(s.to)||closed.has(s.to)) issues.push(prefix+'目标成员无效或输入已经关闭。');
      if (s.kind==='close') closed.add(s.to);
    }
  }
  if (flow.enabled&&!flow.steps.some(s=>s.kind==='receive'&&s.check)) issues.push('启用流程需要至少一个输出校验。');
  return issues;
}
export function sampleData(data: FlowData, test?: TestData, messages = new Map<string, string>()): string {
  if (data.source === 'literal') return data.text;
  const text = data.source === 'input' ? test?.input : data.source === 'answer' ? test?.answer : messages.get(data.stepId);
  if (text == null) return '（请填写样例内容）';
  const lines = text.match(/[^\n]*\n|[^\n]+$/g) ?? [];
  return lines.slice(data.startLine - 1, data.lineCount ? data.startLine - 1 + data.lineCount : undefined).join('');
}
export function samples(flow: CommunicationFlow, test?: TestData) {
  const messages = new Map<string, string>();
  return flow.steps.map(step => {
    const content = step.kind === 'close' ? 'EOF' : step.sample || (step.kind === 'send' ? [step.data, ...step.additionalData].map(data => sampleData(data, test, messages)).join('') : '（请填写样例内容）');
    if (step.kind === 'receive') messages.set(step.id, content);
    return { id: step.id, route: route(flow, step), content };
  });
}
