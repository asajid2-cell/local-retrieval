const { WebSocketServer } = require('ws');
const WebSocket = require('ws');
const wss = new WebSocketServer({ port: 0 }, async () => {
  const port = wss.address().port;
  const client = new WebSocket(`ws://127.0.0.1:${port}/`);
  client.on('message', () => {});
  await new Promise(r => client.on('open', r));
  await new Promise(r => setTimeout(r, 100));
  client.pause();
  await new Promise(r => setTimeout(r, 100));
  const srv = [...wss.clients][0];
  const chunk = Buffer.alloc(256 * 1024, 65);
  for (let i = 0; i < 96; i++) {
    srv.send(chunk);
    if (i % 8 === 7) {
      await new Promise(r => setImmediate(r));
      console.log(`i=${i} sent=${((i+1)*256*1024/1048576).toFixed(1)}MiB bufferedAmount=${srv.bufferedAmount} sockBufSize=${srv._socket.bufferSize} writableLen=${srv._socket.writableLength}`);
    }
  }
  await new Promise(r => setTimeout(r, 1000));
  console.log('FINAL bufferedAmount=', srv.bufferedAmount);
  process.exit(0);
});
