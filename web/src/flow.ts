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
