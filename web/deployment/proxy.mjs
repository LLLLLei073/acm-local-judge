// Pages Functions forwards only the server-owned routes. Nothing here contains a secret;
// ACM_BACKEND_ORIGIN is a server-side Pages setting, never a VITE_* browser variable.
export function backendOrigin(value, publicOrigin) {
  if (!value) throw new Error('missing backend');
  const url = new URL(value);
  if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash || url.origin === publicOrigin) {
    throw new Error('invalid backend');
  }
  return url.origin;
}

export function isServerRoute(path) {
  return path === '/api' || path.startsWith('/api/') || path === '/admin' || path.startsWith('/admin/') || path === '/admin.html' || path === '/healthz';
}

function error(status, message) {
  return Response.json({ error: message }, { status, headers: { 'Cache-Control': 'no-store' } });
}

export async function proxyRequest(context, upstreamFetch = fetch) {
  const { request, env } = context;
  const incoming = new URL(request.url);
  if (!isServerRoute(incoming.pathname)) return context.next();
  let origin;
  try { origin = backendOrigin(env.ACM_BACKEND_ORIGIN, incoming.origin); }
  catch { return error(503, '云端后端尚未配置完成'); }

  // Do not turn arbitrary cross-site requests into trusted same-origin requests.
  const browserOrigin = request.headers.get('Origin');
  if (browserOrigin && browserOrigin !== incoming.origin) return error(403, '请求来源不允许');
  if (!['GET', 'HEAD', 'OPTIONS'].includes(request.method) && request.headers.get('Sec-Fetch-Site') === 'cross-site') {
    return error(403, '请求来源不允许');
  }
  const target = new URL(origin);
  target.pathname = incoming.pathname;
  target.search = incoming.search;
  const headers = new Headers(request.headers);
  for (const name of ['host', 'forwarded', 'x-forwarded-for', 'x-forwarded-host', 'x-forwarded-proto', 'connection', 'transfer-encoding', 'accept-encoding']) headers.delete(name);
  if (browserOrigin) headers.set('Origin', origin);
  // Forward the original byte stream. In particular multipart boundaries and SSE must survive.
  const init = { method: request.method, headers, redirect: 'manual', signal: request.signal };
  if (!['GET', 'HEAD'].includes(request.method)) { init.body = request.body; init.duplex = 'half'; }
  let response;
  try { response = await upstreamFetch(target, init); }
  catch { return error(502, '云端服务暂时无法连接，请稍后重试'); }
  const outgoing = new Headers(response.headers);
  outgoing.set('Cache-Control', 'no-store');
  outgoing.delete('Access-Control-Allow-Origin');
  outgoing.delete('Access-Control-Allow-Credentials');
  const location = outgoing.get('Location');
  if (location) {
    const redirect = new URL(location, target);
    if (redirect.origin === origin) outgoing.set('Location', incoming.origin + redirect.pathname + redirect.search + redirect.hash);
  }
  return new Response(response.body, { status: response.status, statusText: response.statusText, headers: outgoing });
}
