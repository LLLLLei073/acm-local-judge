<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { Plus, Upload, Trash2, Search, Check, ArrowUp, ArrowDown } from 'lucide-vue-next';
import type { TestData, Mode } from '../types';
import { pairTestFiles } from '../domain/workbench';
const tests = defineModel<TestData[]>({ required: true });
const props = defineProps<{ mode: Mode }>();
const bytes=(text:string)=>new TextEncoder().encode(text).length;
const query = ref(''), selected = ref(0), error = ref(''), notice = ref('');
const pending = ref<ReturnType<typeof pairTestFiles> & { files: { name: string; text: string }[] }>();
const rows = computed(() => tests.value.map((t,i)=>({t,i})).filter(({t})=>t.name.toLowerCase().includes(query.value.toLowerCase())));
const current = computed(() => tests.value[selected.value]);
const duplicate = computed(() => tests.value.some((t,i)=>!t.name.trim()||tests.value.some((other,j)=>j!==i&&t.name.trim().toLowerCase()===other.name.trim().toLowerCase())));
watch(() => tests.value.length, length => selected.value = Math.max(0,Math.min(selected.value,length-1)));
function add() {
  let n = tests.value.length+1;
  while (tests.value.some(t=>t.name===String(n).padStart(2,'0'))) n++;
  tests.value.push({name:String(n).padStart(2,'0'),input:'',answer:''}); selected.value=tests.value.length-1; query.value='';
}
function move(offset: number) {
  const from=selected.value, to=from+offset;
  if (to<0||to>=tests.value.length) return;
  const [row]=tests.value.splice(from,1); tests.value.splice(to,0,row!); selected.value=to;
}
async function upload(event: Event) {
  const input=event.target as HTMLInputElement;
  error.value=''; pending.value=undefined;
  try { const files=await Promise.all([...(input.files??[])].map(async file=>({name:file.name,text:await file.text()}))); if(files.length) pending.value={...pairTestFiles(files,props.mode,tests.value),files}; }
  catch(e) { error.value=e instanceof Error?e.message:String(e); }
  finally { input.value=''; }
}
function apply() {
  if (!pending.value) return;
  const next=pairTestFiles(pending.value.files,props.mode,tests.value);
  notice.value=`已载入：新增 ${next.added} 点、替换 ${next.replaced} 点。保存题目后生效。`;
  tests.value=next.tests; pending.value=undefined; selected.value=0;
}
</script>
<template>
  <section class="data-workbench" aria-label="测试数据工作台">
    <div class="section-toolbar"><div><h3>数据集 <span class="count-pill">{{ tests.length }}</span></h3><p class="muted">同名 .in / .ans 配对；第一个测试点公开为题面样例。</p></div><label class="button secondary"><Upload :size="15" />批量载入<input hidden multiple type="file" accept=".in,.ans" @change="upload" /></label><button class="button primary" @click="add"><Plus :size="15" />添加测试点</button></div>
    <p v-if="error" role="alert" class="alert">{{ error }} · 数据未被修改</p><p v-if="notice" class="notice" role="status"><Check :size="15" />{{ notice }}</p><p v-if="duplicate" class="alert">测试点名称不能留空或重复，请修改后保存。</p>
    <div v-if="pending" class="data-import-preview"><b>配对检查完成</b><span>新增 {{ pending.added }} · 将替换 {{ pending.replaced }} · 合并后 {{ pending.tests.length }} 点</span><button class="button primary" @click="apply">应用配对数据</button><button class="text-link" @click="pending=undefined">取消</button></div>
    <div v-if="tests.length" class="data-columns"><aside class="data-index"><div class="search-box"><Search :size="14" /><input v-model="query" aria-label="筛选测试点" placeholder="查找测试点" /></div><button v-for="{t,i} in rows" :key="i" :class="{active:selected===i}" @click="selected=i"><b>{{ t.name || '未命名' }}</b><span>{{ i===0?'公开样例':`测试点 ${i+1}` }}</span></button><p v-if="!rows.length" class="muted empty-small">没有匹配项</p></aside><div v-if="current" class="data-detail"><div class="test-editor-head"><label>名称<input v-model="current.name" aria-label="测试点名称" /></label><span class="paired">{{ selected===0?'公开样例':'隐藏测试点' }}</span><button class="icon-btn" aria-label="上移测试点" :disabled="selected===0" @click="move(-1)"><ArrowUp :size="15" /></button><button class="icon-btn" aria-label="下移测试点" :disabled="selected===tests.length-1" @click="move(1)"><ArrowDown :size="15" /></button><button class="icon-btn danger-text" aria-label="移除测试点" @click="tests.splice(selected,1)"><Trash2 :size="15" /></button></div><div class="sample-grid"><label>输入 · {{ bytes(current.input) }} B<textarea v-model="current.input" rows="16" spellcheck="false"></textarea></label><label>答案 · {{ bytes(current.answer) }} B<textarea v-model="current.answer" rows="16" spellcheck="false"></textarea></label></div><p class="muted">允许合法的空输入或空答案；交互与多进程题由判题程序读取此处数据。</p></div></div>
    <div v-else class="empty-state"><h3>从一组能手算的样例开始</h3><p>先添加公开样例，再覆盖最小规模、极端值与最坏构造。</p><button class="button secondary" @click="add"><Plus :size="15" />创建第一个测试点</button></div>
  </section>
</template>
