import { test } from 'node:test';
import assert from 'node:assert/strict';
import { judgeTools, bruteAssessment } from '../src/domain/judge-tools.ts';
test('required and optional judge tools match the five engine execution modes',()=>{
  assert.deepEqual(judgeTools({mode:'Standard'}).required,[]);
  assert.deepEqual(judgeTools({mode:'Special'}).required,['checker']);
  assert.deepEqual(judgeTools({mode:'Interactive'}).required,['interactor']);
  assert.deepEqual(judgeTools({mode:'Interactive'}).optional,['checker']);
  assert.deepEqual(judgeTools({mode:'Communication',managedCommunication:false}).optional,['checker']);
  assert.deepEqual(judgeTools({mode:'Communication',managedCommunication:true}).required,['manager']);
  assert.deepEqual(judgeTools({mode:'Multiprocess',flow:{enabled:true}}).required,[]);
  assert.deepEqual(judgeTools({mode:'Multiprocess',flow:{enabled:false}}).required,['manager']);
});
test('candidate failures are described without claiming unproven complexity evidence',()=>{
  assert.match(bruteAssessment([{verdict:'CE'}]).text,/先检查/);
  assert.match(bruteAssessment([{verdict:'WA'}]).text,/不能证明/);
  assert.match(bruteAssessment([{verdict:'TLE'}]).text,/flush/);
  assert.match(bruteAssessment([{verdict:'AC'}]).text,/现有数据/);
});
