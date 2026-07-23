process.on('SIGTERM', () => { console.log('HANDLER-FIRED'); setTimeout(()=>process.exit(0), 50); });
setInterval(()=>{}, 1000);
console.log('READY');
