const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('terminal rename waits for authority and carries exact app revision', async () => {
  const html = fs.readFileSync(path.join(__dirname, '../public/index.html'), 'utf8');
  const start = html.indexOf("$('#dorename').onclick = async ()=>{");
  const end = html.indexOf('// compose', start);
  for (const failure of [false, true, 'timeout']) {
    const nodes = {
      '#dorename': {}, '#cancelrename': {}, '#renamestatus': {}, '#renamedlg': { close() {} },
      '#renameinput': { value: 'tab' },
      '#renamenative': { value: 'New native', dataset: { orig: 'Old native' } },
      '#renameapp': { value: 'New app', dataset: { orig: 'Old app' } },
    };
    const posts = [], polls = [], messages = [];
    const context = vm.createContext({
      $: key => nodes[key], renameTarget: 'tab', renameSessionId: 'exact-id', renameTool: 'claude',
      base: '', window: { MUX_DISCOVERY_BASE: '/pc/api/discovery' },
      localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
      crypto: { randomUUID: () => 'rename-fixture' },
      postIntent: async (url, body) => { posts.push(body); return { ok: true, json: async () => ({ id: body.type }) }; },
      pollUploadCmd: async id => { polls.push(id); if(failure==='timeout') return null; if(failure) throw Error('PC refused'); return 'done'; },
      fetch: async () => ({ ok: true, json: async () => ({ rows: [{ id: 'exact-id', tool: 'claude', revision: 'revision-1' }], total: 1 }) }),
      loadSessions() {}, flash: message => messages.push(message),
    });
    vm.runInContext(html.slice(start, end), context);
    await nodes['#dorename'].onclick();
    assert.deepEqual(polls, ['rename', 'setapptitle']);
    assert.equal(posts[1].expectedRevision, 'revision-1');
    assert.equal(posts[1].sessionId, 'exact-id');
    assert.equal(messages[0] === 'Renamed', !failure);
    if(failure) assert.match(nodes['#renamestatus'].textContent, failure==='timeout' ? /outcome unknown/ : /PC refused/);
    assert.equal(nodes['#dorename'].disabled, false);
  }
});

test('terminal new collection waits for creation before exact membership', async () => {
  const html=fs.readFileSync(path.join(__dirname,'../public/index.html'),'utf8');
  const start=html.indexOf('async function submitAddCol(target)');
  const end=html.indexOf('// Per-tab auto-resume',start);
  const posts=[],messages=[];let created=false;
  const nodes={'#addcolstatus':{},'#addcolnew':{value:'New collection'},'#addcoldlg':{querySelectorAll:()=>[],close(){}}};
  const context=vm.createContext({
    $:s=>nodes[s],addColTarget:{name:'tab',sessionId:'exact-chat'},_appLive:true,base:'',window:{},
    addColDeckChoice:()=>({deckId:'main',deckName:'Main'}),appDeckLabel:()=> 'Main',
    localStorage:{getItem:()=>null,setItem(){},removeItem(){}},crypto:{randomUUID:()=> 'fixture-intent'},
    fetch:async (url,options)=>{if(options?.method==='POST'){const body=JSON.parse(options.body);posts.push(body);return {ok:true,json:async()=>({id:body.type})};}return ({ok:true,json:async()=>url.includes('/api/app-commands/')?{status:'done',resultId:'new-id'}:url.includes('/start/collections')?{rows:[{id:'new-id',revision:'membership-r1'}]}:{decks:[{id:'main',label:'Main'}],collections:created?[{id:'new-id',label:'New collection',deckId:'main',revision:'management-r1'}]:[]}});},
    postIntent:async(url,body)=>{posts.push(body);return {ok:true,json:async()=>({id:body.type})};},
    pollUploadCmd:async id=>{if(id==='collectioncreate')created=true;return 'command completed';},
    flash:x=>messages.push(x),pollAppSync(){},
  });
  vm.runInContext(html.slice(start,end),context);
  await context.submitAddCol('New collection');
  assert.deepEqual(posts.map(p=>p.type),['collectioncreate','addtocollection']);
  assert.equal(posts[1].collectionId,'new-id');
  assert.equal(posts[1].expectedCollectionRevision,'membership-r1');
  assert.equal(posts[1].sessionId,'exact-chat');
  assert.match(messages[0],/^Added/);
  posts.length=0;
  context.addColTarget={name:'unlinked-tab'};
  await context.submitAddCol('Must not create');
  assert.equal(posts.length,0,'unlinked tabs must not create containers');
  assert.match(messages.at(-1),/Exact chat identity required/);
});

test('upload timeout and reload retry preserve original input intent and payload', async () => {
  const html=fs.readFileSync(path.join(__dirname,'../public/index.html'),'utf8');
  const start=html.indexOf('async function insertUploadIntoPrompt(');
  const end=html.indexOf('async function wsAdd(',start);
  const records=new Map(),posts=[];
  let complete=false,serial=0;
  const makeContext=()=>vm.createContext({
    current:'tab',base:'',findSession:()=>({sessionId:'chat',generationId:'generation'}),flash(){},
    localStorage:{getItem:key=>records.get(key)||null,setItem:(key,value)=>records.set(key,value),removeItem:key=>records.delete(key)},
    crypto:{randomUUID:()=> 'intent-'+(++serial)},
    postIntent:async(url,body)=>{posts.push(JSON.parse(JSON.stringify(body)));return {ok:true,json:async()=>({id:'same-command'})};},
    pollUploadCmd:async()=>complete?'inserted':null,
  });
  const first=makeContext();vm.runInContext(html.slice(start,end),first);
  await assert.rejects(first.insertUploadIntoPrompt({id:'upload',name:'file.txt',keep:false},'path'),/outcome unknown/);
  assert.equal(records.size,1);
  const reloaded=makeContext();vm.runInContext(html.slice(start,end),reloaded);
  complete=true;
  await reloaded.insertUploadIntoPrompt({id:'upload',name:'file.txt',keep:true},'path');
  assert.deepEqual(posts[0],posts[1],'retry preserves complete original payload even if keep changes');
  assert.equal(serial,1);
  assert.equal(records.size,0);
});

test('unconfirmed upload insertion keeps dialog open with persistent reconciliation status', async () => {
  const html=fs.readFileSync(path.join(__dirname,'../public/index.html'),'utf8');
  const start=html.indexOf('async function wsAdd(');
  const end=html.indexOf('async function wsKeep(',start);
  const status={textContent:''};
  let closed=false,focused=false;
  const context=vm.createContext({
    $:selector=>selector==='#wsinsertstatus'?status:{close(){closed=true;}},
    term:{focus(){focused=true;}},flash(){},
    insertUploadIntoPrompt:async()=>{throw new Error('insertion outcome unknown; retry reconciles the original request');},
  });
  vm.runInContext(html.slice(start,end),context);
  await context.wsAdd({id:'upload'},'path');
  assert.equal(closed,false);
  assert.equal(focused,false);
  assert.match(status.textContent,/Insertion not confirmed:.*outcome unknown.*original request/);
});

test('container busy reset preserves the Main delete prohibition', () => {
  const html = fs.readFileSync(path.join(__dirname, '../public/projects.html'), 'utf8');
  const start = html.indexOf('function setContainerMutationBusy(');
  const end = html.indexOf('function queueContainerMutation(', start);
  assert.ok(start >= 0 && end > start, 'runtime busy helper exists');
  const mainDelete = { id: '', disabled: true, title: 'Main cannot be deleted', dataset: {}, getAttribute: name => name === 'title' ? 'Main cannot be deleted' : null };
  const ordinary = { id: '', disabled: false, title: 'Delete deck', dataset: {}, getAttribute: name => name === 'title' ? 'Delete deck' : null };
  const dialog = { dataset: {}, querySelectorAll: () => [mainDelete, ordinary] };
  const status = { textContent: '' };
  const context = vm.createContext({ $: selector => selector === '#containerdlg' ? dialog : status });
  vm.runInContext(html.slice(start, end), context);
  vm.runInContext('setContainerMutationBusy(true)', context);
  assert.equal(ordinary.disabled, true);
  vm.runInContext('setContainerMutationBusy(false)', context);
  assert.equal(ordinary.disabled, false);
  assert.equal(mainDelete.disabled, true, 'ending a mutation must not enable deletion of Main');
});
