import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { backendOrigin, isServerRoute, proxyRequest } from '../deployment/proxy.mjs';

const origin = 'https://judge.pages.dev';
const backend = 'https://judge.onrender.com';
const context = (path, init = {}, env = { ACM_BACKEND_ORIGIN: backend }) => ({
  request: new Request(origin + path, init), env, next: () => new Response('static'),
});

test('Pages routes cover APIs, admin gates and health without invoking functions for assets', async () => {
  for (const path of ['/api', '/api/users/me', '/admin', '/admin/', '/admin.html', '/healthz']) assert.ok(isServerRoute(path), path);
  for (const path of ['/', '/assets/admin.js', '/administrator', '/api-other']) assert.equal(isServerRoute(path), false, path);
  assert.equal(await (await proxyRequest(context('/assets/main.js'), () => { throw new Error('unexpected fetch'); })).text(), 'static');
  const routes = JSON.parse(await readFile(new URL('../public/_routes.json', import.meta.url), 'utf8'));
  assert.deepEqual(routes, { version: 1, include: ['/api', '/api/*', '/admin', '/admin/*', '/admin.html', '/healthz'], exclude: [] });
});

test('backend configuration fails closed without leaking configuration', async () => {
  assert.equal(backendOrigin(backend + '/', origin), backend);
  for (const value of [undefined, 'no-url', 'http://example.com', origin, backend + '/api', backend + '?token=secret', 'https://user:secret@example.com', backend + '#secret']) {
    const response = await proxyRequest(context('/admin', {}, { ACM_BACKEND_ORIGIN: value }));
    assert.equal(response.status, 503); assert.equal(response.headers.get('Cache-Control'), 'no-store');
    assert.ok(!(await response.text()).includes('secret'));
  }
});

test('login body and cookie forwarding preserves same-origin authentication', async () => {
  const response = await proxyRequest(context('/api/users/login?q=1', {
    method: 'POST', body: '{"studentId":"test"}',
    headers: { Origin: origin, Cookie: 'acm_user=example', 'Content-Type': 'application/json', 'X-Forwarded-For': 'forged', Forwarded: 'forged' },
  }), async (url, init) => {
    assert.equal(url.href, backend + '/api/users/login?q=1');
    assert.equal(init.redirect, 'manual');
    assert.equal(init.headers.get('Origin'), backend);
    assert.equal(init.headers.get('Cookie'), 'acm_user=example');
    assert.equal(init.headers.get('X-Forwarded-For'), null);
    assert.equal(init.headers.get('Forwarded'), null);
    assert.equal(await new Response(init.body).text(), '{"studentId":"test"}');
    return new Response('{}', { headers: { 'Set-Cookie': 'acm_user=example; HttpOnly; SameSite=Strict; Secure', 'Cache-Control': 'public' } });
  });
  assert.match(response.headers.get('Set-Cookie'), /HttpOnly/);
  assert.equal(response.headers.get('Cache-Control'), 'no-store');
});

test('cross-site origins cannot be rewritten to a trusted origin', async () => {
  for (const headers of [{ Origin: 'https://attacker.example' }, { Origin: 'null' }, { 'Sec-Fetch-Site': 'cross-site' }]) {
    const response = await proxyRequest(context('/api/submissions', { method: 'POST', headers }), () => { throw new Error('must not fetch'); });
    assert.equal(response.status, 403);
  }
});

test('admin.html always passes through backend authentication gate', async () => {
  for (const path of ['/admin', '/admin.html']) {
    const response = await proxyRequest(context(path), async url => {
      assert.equal(url.pathname, path);
      return new Response('gate', { status: 401 });
    });
    assert.equal(response.status, 401); assert.equal(await response.text(), 'gate');
  }
});

test('multipart upload bytes are preserved', async () => {
  const body = new Uint8Array([0, 255, 13, 10, 128]);
  await proxyRequest(context('/api/problems/id/data', { method: 'POST', headers: { 'Content-Type': 'multipart/form-data; boundary=abc' }, body }), async (_, init) => {
    assert.equal(init.headers.get('Content-Type'), 'multipart/form-data; boundary=abc');
    assert.deepEqual(new Uint8Array(await new Response(init.body).arrayBuffer()), body);
    return new Response(null, { status: 204 });
  });
});

test('SSE streams are delivered before the upstream closes', async () => {
  let controller;
  const stream = new ReadableStream({ start(value) { controller = value; } });
  const response = await proxyRequest(context('/api/submissions/id/events'), async () => new Response(stream, { headers: { 'Content-Type': 'text/event-stream' } }));
  assert.equal(response.headers.get('Content-Type'), 'text/event-stream');
  const reader = response.body.getReader();
  controller.enqueue(new TextEncoder().encode('data: running\n\n'));
  assert.equal(new TextDecoder().decode((await reader.read()).value), 'data: running\n\n');
  controller.close(); assert.equal((await reader.read()).done, true);
});

test('backend redirects remain on public site; upstream failures are explicit and private', async () => {
  const response = await proxyRequest(context('/admin.html'), async () => new Response(null, { status: 302, headers: { Location: backend + '/admin?q=1' } }));
  assert.equal(response.status, 302); assert.equal(response.headers.get('Location'), origin + '/admin?q=1');
  const failure = await proxyRequest(context('/api/users/me'), async () => { throw new Error('private infrastructure detail'); });
  assert.equal(failure.status, 502); assert.ok(!(await failure.text()).includes('private'));
});
