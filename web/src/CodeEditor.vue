<script setup lang="ts">
import { ref, onMounted, onBeforeUnmount, watch } from 'vue';
import * as monaco from 'monaco-editor/esm/vs/editor/editor.api.js';
import 'monaco-editor/esm/vs/basic-languages/cpp/cpp.contribution.js';
import EditorWorker from 'monaco-editor/esm/vs/editor/editor.worker.js?worker';
(self as unknown as { MonacoEnvironment: object }).MonacoEnvironment = { getWorker: () => new EditorWorker() };
const props = defineProps<{ modelValue: string; readOnly?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [string] }>();
const host = ref<HTMLElement>();
let editor: monaco.editor.IStandaloneCodeEditor | undefined;
onMounted(() => {
  monaco.editor.defineTheme('acm-light', { base: 'vs', inherit: true, rules: [], colors: { 'editor.background': '#fcfcfa', 'editorLineNumber.foreground': '#a6ada8', 'editor.lineHighlightBackground': '#f2f5f1', 'editor.selectionBackground': '#dce9e1' } });
  editor = monaco.editor.create(host.value!, { value: props.modelValue, language: 'cpp', theme: 'acm-light', automaticLayout: true, fontSize: 14, fontFamily: "'Cascadia Code', Consolas, monospace", minimap: { enabled: false }, scrollBeyondLastLine: false, lineNumbersMinChars: 3, padding: { top: 20 }, tabSize: 4, readOnly: props.readOnly, wordWrap: 'on' });
  editor.onDidChangeModelContent(() => emit('update:modelValue', editor!.getValue()));
});
watch(() => props.modelValue, value => { if (editor && editor.getValue() !== value) editor.setValue(value); });
onBeforeUnmount(() => { const model = editor?.getModel(); editor?.dispose(); model?.dispose(); });
</script>
<template><div ref="host" class="code-editor" aria-label="C++23 代码编辑器"></div></template>
