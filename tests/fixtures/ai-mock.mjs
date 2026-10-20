// Local deterministic OpenAI-compatible fixture; never forwards requests or uses real credentials.
import { createServer } from 'node:http';
const port=Number(process.env.ACM_AI_FIXTURE_PORT||27129);
const server=createServer(async (request,response)=>{
  response.setHeader('Access-Control-Allow-Origin','http://127.0.0.1:27128');
  response.setHeader('Access-Control-Allow-Headers',request.headers['access-control-request-headers']||'Content-Type, Authorization');
  response.setHeader('Access-Control-Allow-Methods','GET, POST, OPTIONS');
  if(request.method==='OPTIONS'){response.writeHead(204).end();return;}
  console.log(request.method,request.url);
  response.setHeader('Content-Type','application/json; charset=utf-8');
  if(request.headers.authorization!=='Bearer fixture-key'){response.writeHead(401).end(JSON.stringify({error:{message:'Invalid fixture key'}}));return;}
  if(request.url?.endsWith('/models')){response.end(JSON.stringify({object:'list',data:[{id:'refactor-test-model',object:'model'}]}));return;}
  try {
    const chunks=[];let length=0;
    for await(const chunk of request){length+=chunk.length;if(length>2*1024*1024){response.writeHead(413).end();return;}chunks.push(chunk);}
    const body=JSON.parse(Buffer.concat(chunks).toString('utf8'));
    const tool=body.tools?.[0]?.function;
    const schema=tool?.parameters??body.response_format?.json_schema?.schema;
    let message={role:'assistant',content:'ok'},finish='stop';
    if(schema){
      const fields=Object.keys(schema.properties??{});
      const data=fields.includes('rating')?{rating:1200,reason:'本地固定响应：基础实现与边界处理'}:fields.includes('tests')?{tests:[{input:'1 2\n'},{input:'1000000 -1000000\n'}]}:{statement:'## 题目描述\n输入两个整数并输出它们的和。\n\n## 输入格式\n一行两个整数，绝对值不超过 1000000。\n\n## 输出格式\n输出两数之和。'};
      if(tool){message={role:'assistant',content:null,tool_calls:[{id:'fixture-call',type:'function',function:{name:tool.name,arguments:JSON.stringify(data)}}]};finish='tool_calls';}
      else message={role:'assistant',content:JSON.stringify(data)};
    }
    response.end(JSON.stringify({id:'fixture-response',object:'chat.completion',created:1,model:'refactor-test-model',choices:[{index:0,message,finish_reason:finish}],usage:{prompt_tokens:1,completion_tokens:1,total_tokens:2}}));
  } catch {response.writeHead(400).end(JSON.stringify({error:{message:'Invalid fixture request'}}));}
});
server.listen(port,'127.0.0.1',()=>console.log(`AI fixture ready: http://127.0.0.1:${port}/v1; key: fixture-key`));
process.on('SIGINT',()=>server.close());
