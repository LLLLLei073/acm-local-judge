import { test } from 'node:test';
import assert from 'node:assert/strict';
import { InertScope } from '../src/services/inert-scope.ts';
test('closing a stacked preview restores its underlying dialog even when found by two paths',()=>{
  const root={inert:false},dialog={inert:false},scope=new InertScope();
  scope.disable(root);scope.disable(dialog);scope.disable(dialog);
  assert(dialog.inert);scope.restore();assert.equal(root.inert,false);assert.equal(dialog.inert,false);
});
test('preexisting inert nodes stay inert and later modal sessions get a fresh baseline',()=>{
  const preexisting={inert:true},ordinary={inert:false},scope=new InertScope();
  scope.disable(preexisting);scope.disable(ordinary);scope.restore();assert(preexisting.inert);assert.equal(ordinary.inert,false);
  scope.disable(ordinary);scope.restore();assert.equal(ordinary.inert,false);
});
