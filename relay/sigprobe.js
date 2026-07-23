const cp=require('child_process');
const c=cp.spawn(process.execPath,['sigprobe-child.js'],{stdio:['ignore','pipe','pipe']});
let out=''; c.stdout.on('data',d=>out+=d);
c.on('exit',(code,sig)=>console.log('EXIT code=',code,'signal=',sig,'stdout=',JSON.stringify(out)));
setTimeout(()=>{ console.log('sending SIGTERM'); c.kill('SIGTERM'); },500);
