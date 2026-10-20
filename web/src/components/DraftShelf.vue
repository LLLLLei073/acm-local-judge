<script setup lang="ts">
import { ref } from 'vue';
import { FileClock, ArrowRight, Trash2 } from 'lucide-vue-next';
import type { SavedProblemDraft } from '../domain/problem-drafts';
defineProps<{entries:SavedProblemDraft[]}>();
defineEmits<{restore:[id:string];discard:[id:string]}>();
const expanded=ref(false);
</script>
<template><section v-if="entries.length" class="draft-shelf"><header><div><FileClock :size="16" /><strong>本机草稿 <span class="count-pill">{{ entries.length }}</span></strong><span>按题目独立保存，保存一题不会清除其他草稿。</span></div><button class="text-link" @click="expanded=!expanded">{{ expanded?'收起':'查看恢复点' }}</button></header><div v-if="expanded" class="draft-shelf-list"><article v-for="entry in entries" :key="entry.id"><div><b>{{ entry.problem.title || '未命名新题' }}</b><small>{{ entry.updatedAt?new Date(entry.updatedAt).toLocaleString('zh-CN'):'旧版草稿' }} · {{ entry.problem.id?'编辑已有题目':'新建题目' }}</small></div><button class="button secondary" @click="$emit('restore',entry.id)">恢复<ArrowRight :size="13" /></button><button class="icon-btn danger-text" :aria-label="'丢弃草稿 '+(entry.problem.title||'未命名新题')" @click="$emit('discard',entry.id)"><Trash2 :size="14" /></button></article></div></section></template>
<style scoped>
.draft-shelf{border:1px solid #d7e2c8;background:#f6faef;border-radius:6px;margin:0 0 20px;padding:16px 20px}.draft-shelf header,.draft-shelf header>div{display:flex;align-items:center;gap:10px;flex-wrap:wrap}.draft-shelf header>div{margin-right:auto}.draft-shelf header strong{font-size:12px;color:#527344}.draft-shelf header span{font-size:11px;color:#6e815e}.draft-shelf-list{margin-top:15px;border-top:1px solid #dce6cf}.draft-shelf-list article{display:flex;align-items:center;gap:10px;padding:14px 0;border-bottom:1px solid #e0e8d5}.draft-shelf-list article>div{flex:1;min-width:0}.draft-shelf-list b{font-size:12px;font-weight:500;overflow-wrap:anywhere}.draft-shelf-list small{display:block;font-size:10px;color:#748662;margin-top:7px}
</style>
