// LangChain 验题助手：难度评级建议与验证示例生成。
// ChatOpenAI 按需动态加载，只有真正调用 AI 时才拉取相应代码；配置存本浏览器 localStorage。
import { z } from 'zod';
import { modeInfo, type Problem } from './types';

export interface AiConfig { base: string; key: string; model: string }
const KEY_BASE = 'acm-ai-base', KEY_KEY = 'acm-ai-key', KEY_MODEL = 'acm-ai-model';

export function loadAiConfig(): AiConfig {
  return { base: localStorage.getItem(KEY_BASE) ?? '', key: localStorage.getItem(KEY_KEY) ?? '', model: localStorage.getItem(KEY_MODEL) ?? '' };
}
export function saveAiConfig(c: AiConfig) {
  localStorage.setItem(KEY_BASE, c.base.trim());
  localStorage.setItem(KEY_KEY, c.key.trim());
  localStorage.setItem(KEY_MODEL, c.model.trim());
}
export const aiReady = (c: AiConfig) => !!(c.base.trim() && c.key.trim() && c.model.trim());

async function chat(temperature = 0) {
  const { ChatOpenAI } = await import('@langchain/openai');
  const c = loadAiConfig();
  if (!aiReady(c)) throw new Error('请先在「运行环境」中配置 AI（Base URL / API Key / 模型），输入后自动保存');
  return new ChatOpenAI({ model: c.model.trim(), apiKey: c.key.trim(), temperature, maxRetries: 1, configuration: { baseURL: c.base.trim(), dangerouslyAllowBrowser: true } });
}

// 把底层网络/SDK 错误翻译成可操作的中文提示。
function friendly(e: unknown): never {
  const msg = e instanceof Error ? e.message : String(e);
  if (/Failed to fetch|NetworkError|Load failed|fetch failed|Connection error|ECONNREFUSED|ETIMEDOUT|ENOTFOUND/i.test(msg)) throw new Error('无法连接 AI 服务：请检查 Base URL 是否正确、网络是否可达；浏览器直连要求服务支持跨域（CORS），DeepSeek / Kimi / 通义官方接口均可直连');
  if (/\b401\b|\b403\b|invalid[ _-]api[ _-]?key|unauthorized/i.test(msg)) throw new Error('AI 服务拒绝了 API Key：请检查 Key 是否正确、账户是否有额度');
  if (/\b404\b/i.test(msg)) throw new Error('AI 服务返回 404：Base URL 路径不对，通常应以 /v1 结尾（如 https://api.deepseek.com/v1）');
  if (/\b429\b|rate[ _-]?limit/i.test(msg)) throw new Error('AI 服务限流（429）：请稍后再试或检查账户额度');
  if (/model.{0,20}(not|does not).{0,20}exist|invalid model/i.test(msg)) throw new Error('AI 服务不认识该模型名：请检查模型 ID（如 deepseek-chat）');
  throw new Error('AI 调用失败：' + msg);
}

function describe(p: Problem): string {
  const info = modeInfo(p.mode);
  return [
    `题目标题：${p.title}`,
    `评测类型：${info.label}（${info.description}）`,
    `题面：\n${p.statement}`,
    p.protocol ? `读写协议：${p.protocol}` : '',
    `测试点数量：${p.tests.length}`,
    `资源限制：CPU ${p.limits.cpuMs}ms / 墙钟 ${p.limits.wallMs}ms / 内存 ${p.limits.memoryMiB}MiB / 输出 ${p.limits.outputKiB}KiB`,
    p.mode === 'Multiprocess' ? `进程数：${p.processCount}` : '',
  ].filter(Boolean).join('\n');
}

const RatingSuggestion = z.object({ rating: z.number().int(), reason: z.string().describe('一句话理由，中文，不超过 60 字') });
export async function suggestRating(p: Problem): Promise<{ rating: number; reason: string }> {
  try {
    const judge = (await chat()).withStructuredOutput(RatingSuggestion);
    const out = await judge.invoke([
      ['system', '你是算法竞赛出题专家，熟悉 Codeforces 难度体系。根据题面、评测类型、算法难度和实现量，评估这道题的难度 rating：100–3500 之间、按 100 取整（800≈入门，1200≈简单，1600≈需基础算法，2000≈需较熟练算法，2600≈难题，3200≈极难）。只依据题目本身，不要臆造不存在的约束。'],
      ['human', describe(p)],
    ]);
    const rating = Math.min(3500, Math.max(100, Math.round(out.rating / 100) * 100));
    return { rating, reason: out.reason };
  } catch (e) { friendly(e); }
}

const HarshTests = z.object({ tests: z.array(z.object({ input: z.string().describe('完整测试输入，严格符合输入格式，以换行结尾') })).max(8).describe('最多 8 条极端测试输入') });
export async function generateHarshTests(p: Problem): Promise<{ tests: { input: string }[] }> {
  try {
    const judge = (await chat(0.3)).withStructuredOutput(HarshTests);
    const out = await judge.invoke([
      ['system', '你是算法竞赛的 hack 数据专家。任务：为这道题生成最多 8 条「条件范围内最严苛」的测试输入，用于卡掉复杂度过高或边界处理不当的提交：\n' +
        '1. 先从题面提取输入约束（数据范围、变量类型）；不得仅凭时间限制臆造未写明的数据范围；约束不足时只生成明确合法的小规模输入，无法确定合法性时返回空 tests。\n' +
        '2. 每条输入都要顶格或贴边：最大规模、最大值/最小值、全相同/全逆序/严格递增等最坏构造；至少一条覆盖边界（最小 n、零、负数、重复极值）。\n' +
        '3. 输入必须严格符合题面输入格式（行数、分隔、顺序），以换行结尾，不含任何解释文字。\n' +
        '4. 针对性构造：n 取最大时配合最坏取值分布，让 O(n²) 或常数大的做法超时。'],
      ['human', describe(p) + `\n时间限制：${p.limits.cpuMs}ms（目标：让复杂度不达标的做法在此限制内超时）`],
    ]);
    return { tests: out.tests.map(t => ({ input: t.input })) };
  } catch (e) { friendly(e); }
}

// 出题第一步最费时的是写题面：按主题起草一版结构完整的 Markdown 题面（描述/输入/输出/样例/提示）。
const StatementDraft = z.object({ statement: z.string().describe('完整 Markdown 题面，以 ## 题目描述 开头') });
export async function draftStatement(topic: string, modeLabel: string, modeDescription: string): Promise<string> {
  try {
    const judge = (await chat(0.4)).withStructuredOutput(StatementDraft);
    const out = await judge.invoke([
      ['system', '你是算法竞赛出题专家，为校内 OJ（C++23）起草题面草稿。结构固定：## 题目描述（背景清晰、无歧义）→ ## 输入格式（逐项列出，数据范围具体且与 1000–2000ms 时限匹配）→ ## 输出格式 → ## 样例（1–2 组，用 ```text 代码块分别给出输入与输出，样例必须严格符合你写的格式与约束）→ ## 提示（可选，算法方向或注意点，不直接给做法）。评测类型：' + modeLabel + '（' + modeDescription + '）；若为交互/通信类，题面需写清交互协议与 flush 要求。'],
      ['human', '题目主题与要求：' + topic],
    ]);
    return out.statement.trim();
  } catch (e) { friendly(e); }
}

// 拉取服务端可用模型列表（OpenAI 兼容的 GET /models）。
export async function fetchModels(): Promise<string[]> {
  const c = loadAiConfig();
  if (!c.base.trim() || !c.key.trim()) throw new Error('请先填写 Base URL 与 API Key，再获取模型列表');
  const url = c.base.trim().replace(/\/+$/, '') + '/models';
  let res: Response;
  try { res = await fetch(url, { headers: { Authorization: 'Bearer ' + c.key.trim() } }); }
  catch { throw new Error('无法连接 AI 服务：请检查 Base URL 与网络；浏览器直连要求服务支持跨域（CORS）'); }
  if (res.status === 401 || res.status === 403) throw new Error('获取模型列表被拒绝（' + res.status + '）：请检查 API Key');
  if (!res.ok) throw new Error('获取模型列表失败（HTTP ' + res.status + '）：该服务可能不支持 /models 接口，可手动填写模型名');
  const data = await res.json().catch(() => null);
  const ids: string[] = Array.isArray(data?.data) ? data.data.map((m: { id?: string }) => m.id).filter((x: unknown): x is string => typeof x === 'string' && x.length > 0) : [];
  if (!ids.length) throw new Error('服务返回了空的模型列表，可手动填写模型名');
  return [...new Set(ids)].sort();
}

// 测试连接：发一条极小的补全请求，验证 Base URL / Key / 模型与跨域是否可用。
export async function testConnection(): Promise<string> {
  try {
    const model = await chat();
    await model.invoke([['human', 'ping']]);
    return loadAiConfig().model.trim();
  } catch (e) { friendly(e); }
}
