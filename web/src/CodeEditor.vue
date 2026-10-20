<script setup lang="ts">
import { ref, onMounted, onBeforeUnmount, watch, computed } from 'vue';
import * as monaco from 'monaco-editor/esm/vs/editor/editor.api.js';
import 'monaco-editor/esm/vs/basic-languages/cpp/cpp.contribution.js';
import EditorWorker from 'monaco-editor/esm/vs/editor/editor.worker.js?worker';
(self as unknown as { MonacoEnvironment: object }).MonacoEnvironment = { getWorker: () => new EditorWorker() };
const props = defineProps<{ modelValue: string; readOnly?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [string]; submit: [] }>();
const host = ref<HTMLElement>();
const wrap = ref(true), fontSize = ref(14), position = ref({ lineNumber: 1, column: 1 });
const lines = computed(() => props.modelValue.split('\n').length);
let editor: monaco.editor.IStandaloneCodeEditor | undefined;
onMounted(() => {
  monaco.editor.defineTheme('acm-light', { base: 'vs', inherit: true, rules: [], colors: { 'editor.background': '#fcfcfa', 'editorLineNumber.foreground': '#a6ada8', 'editor.lineHighlightBackground': '#f2f5f1', 'editor.selectionBackground': '#dce9e1' } });
  editor = monaco.editor.create(host.value!, { value: props.modelValue, language: 'cpp', theme: 'acm-light', automaticLayout: true, fontSize: 14, fontFamily: "'Cascadia Code', Consolas, monospace", minimap: { enabled: false }, scrollBeyondLastLine: false, lineNumbersMinChars: 3, padding: { top: 20 }, tabSize: 4, readOnly: props.readOnly, wordWrap: 'on' });
  editor.onDidChangeModelContent(() => emit('update:modelValue', editor!.getValue()));
  editor.onDidChangeCursorPosition(event => position.value=event.position);
  editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter,()=>{ if(!props.readOnly) emit('submit'); });
});
watch(() => props.modelValue, value => { if (editor && editor.getValue() !== value) editor.setValue(value); });
watch(() => props.readOnly, readOnly=>editor?.updateOptions({readOnly}));
watch([wrap,fontSize],()=>editor?.updateOptions({wordWrap:wrap.value?'on':'off',fontSize:fontSize.value}));
onBeforeUnmount(() => { const model = editor?.getModel(); editor?.dispose(); model?.dispose(); });
</script>
<template><div class="editor-surface"><div class="editor-preferences"><span>{{ readOnly?'只读源码':'C++23 编辑器' }} · {{ lines }} 行</span><label class="checkbox"><input type="checkbox" v-model="wrap" />自动换行</label><label>字号<select v-model.number="fontSize" aria-label="编辑器字号"><option :value="12">12</option><option :value="14">14</option><option :value="16">16</option><option :value="18">18</option></select></label><button class="text-link" @click="editor?.focus(); editor?.setSelection(editor.getModel()!.getFullModelRange())">全选代码</button></div><div ref="host" class="code-editor" aria-label="C++23 代码编辑器"></div><div class="editor-position">Ln {{ position.lineNumber }}, Col {{ position.column }}<span>UTF-8 · {{ readOnly?'只读':'Tab = 4' }}</span></div></div></template>
<style scoped>
.editor-preferences{display:flex;align-items:center;gap:14px;flex-wrap:wrap;padding:9px 16px;background:#f3f7ed;border-bottom:1px solid #e2e8d8;font-size:10px;color:#6d815e}.editor-preferences>span{margin-right:auto}.editor-preferences label{font-size:10px;display:flex;flex-direction:row;align-items:center;gap:5px}.editor-preferences select{padding:3px;font-size:10px}.editor-position{display:flex;justify-content:space-between;padding:7px 16px;background:#f6f8f0;font:10px 'Cascadia Code',Consolas,monospace;color:#6c7f60}
</style>
