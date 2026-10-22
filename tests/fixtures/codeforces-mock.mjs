// Browser acceptance fixture; never use this endpoint for real account verification.
import http from 'node:http';
import fs from 'node:fs';
const port = Number(process.env.CF_FIXTURE_PORT || 27131);
const statePath = process.env.CF_FIXTURE_STATE;
if (!statePath) throw new Error('CF_FIXTURE_STATE is required');
http.createServer((req, res) => {
  const state = JSON.parse(fs.readFileSync(statePath, 'utf8').replace(/^\uFEFF/, ''));
  const url = new URL(req.url, `http://127.0.0.1:${port}`);
  res.setHeader('Content-Type', 'application/json');
  if (state.fail) return res.end(JSON.stringify({ status: 'FAILED', comment: 'fixture: CF is temporarily unavailable' }));
  if (url.pathname === '/user.info') return res.end(JSON.stringify({ status: 'OK', result: [{ handle: 'cf_browser', rating: 1620, rank: 'expert', lastName: state.lastName }] }));
  const from = Number(url.searchParams.get('from') || 1);
  const rows = Array.from({ length: 63 }, (_, i) => ({ id: 10000-i, contestId: 100, creationTimeSeconds: 1700000000+i*3600, problem: { contestId:100, index:i%2?'A':'B', name:i%2?'Sum of Numbers':'Ordered Messages', rating:800, tags:['math'] }, verdict:i%3?'WRONG_ANSWER':'OK', programmingLanguage:'GNU C++23', timeConsumedMillis:31, memoryConsumedBytes:1048576 }));
  res.end(JSON.stringify({ status: 'OK', result: rows.slice(from-1, from-1+1000) }));
}).listen(port, '127.0.0.1', () => console.log(`CF fixture http://127.0.0.1:${port}`));
