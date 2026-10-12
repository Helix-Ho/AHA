// Run from the repository root: node src/topo/checks/map/topo-map-checks.cjs
// Headless state/geometry checks; native Eto/WebView appearance is tested separately.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');

class Element {
  constructor() {
    this.children = []; this.style = {}; this.dataset = {}; this.attributes = {};
    this.clientWidth = 680; this.clientHeight = 300; this.disabled = false; this.textContent = '';
    this.listeners = new Map(); this.left = 0; this.top = 0;
  }
  replaceChildren() { this.children = []; }
  appendChild(element) { this.children.push(element); }
  setAttribute(key, value) { this.attributes[key] = value; }
  getBoundingClientRect() { return { left: this.left, top: this.top }; }
  addEventListener(type, listener, options) { this.listeners.set(type, { listener, options }); }
  dispatch(type, event) { this.listeners.get(type)?.listener(event); }
  setPointerCapture() {}
  closest() { return false; }
}
const elements = new Map(), messages = [];
let clock = 0, nextTimer = 0;
const timers = new Map();
const get = id => { if (!elements.has(id)) elements.set(id, new Element()); return elements.get(id); };
const context = vm.createContext({
  document: { getElementById: get, createElement: () => new Element(), createElementNS: () => new Element() },
  window: { eto: { postMessage: value => messages.push(JSON.parse(value)) } },
  performance: { now: () => clock },
  setTimeout: action => { const id = ++nextTimer; timers.set(id, action); return id; }, clearTimeout(id) { timers.delete(id); },
});
const html = fs.readFileSync(path.resolve(__dirname, '../../api/rhino/TopoMap.html'), 'utf8');
vm.runInContext(html.replaceAll('__AHA_MAP_GENERATION__', '42').match(/<script>([\s\S]*)<\/script>/)[1], context);
let checks = 0;
function run(code) { return vm.runInContext(code, context); }
function check(code) { assert.equal(run(code), true, code); checks++; }
function scenario(action) { action(); checks++; }

check('inRing([2,2],[[0,0],[5,0],[5,5],[0,5]])');
check('!inRing([6,2],[[0,0],[5,0],[5,5],[0,5]])');
run("aha=window.aha;aha.overlay({rings:[],parcels:[{id:'pnu:a',polygons:[[[[0,0],[5,0],[5,5],[0,5]],[[1,1],[3,1],[3,3],[1,3]]]]}],bounds:null});");
check("selectedAt([4,4]).id==='pnu:a'");
check('!selectedAt([2,2])');
run('aha.fit({bounds:[126.9,37.5,126.91,37.51]})');
check('Number.isInteger(z)&&z>=7&&z<=19&&Math.abs(lon-126.905)<1e-10');
run('aha.overlay({nearRings:[[[0,0],[2,0],[2,2],[0,0]]],nearValid:true,bounds:[0,0,2,2]})');
check("svg.children.some(p=>p.attributes.class==='near')&&document.getElementById('legend').textContent.includes('근접 영역')");
run('aha.overlay({nearRings:[[[0,0],[2,0],[2,2],[0,0]]],nearValid:false,bounds:[0,0,2,2]})');
check("svg.children.some(p=>p.attributes.class==='near-invalid')&&!svg.children.some(p=>p.attributes.class==='near')");
run('aha.overlay({nearRings:[],bounds:null})');
check("!document.getElementById('legend').textContent.includes('근접')");


// C# is the only network boundary. The viewer requests integer WMTS tile IDs.
run('aha.center({lon:127,lat:37.5,zoom:16})');
scenario(() => {
  assert.equal(messages.at(-1).type, 'tiles');
  assert.ok(messages.at(-1).tiles.length > 0);
  assert.ok(messages.at(-1).tiles.every(t => Number.isInteger(t.x) && Number.isInteger(t.y) && t.z === 16));
  assert.ok(!html.includes('fetch(') && !html.includes('XMLHttpRequest'));
});
run("aha.tile(tiles.children[0].dataset.id,'data:image/png;base64,fixture')");
check("tiles.children[0].src==='data:image/png;base64,fixture'");

function clickCenter() {
  run('el.onpointerdown({button:0,target:el,pointerId:1,clientX:340,clientY:150});el.onpointerup({button:0,clientX:340,clientY:150});');
}
scenario(() => {
  const before = messages.length;
  for (const button of [1, 2])
    run(`el.onpointerdown({button:${button},target:el,pointerId:1,clientX:340,clientY:150});el.onpointermove({clientX:380,clientY:170});el.onpointerup({button:${button},clientX:380,clientY:170});`);
  assert.equal(run('drag'), null);
  assert.equal(messages.length, before); // Secondary clicks neither pick/remove nor pan.
});
scenario(() => {
  const before = messages.length;
  run('el.onpointerdown({button:0,target:el,pointerId:1,clientX:340,clientY:150});el.onpointerup({button:2,clientX:340,clientY:150});');
  assert.notEqual(run('drag'), null); // A secondary release cannot finish a primary gesture.
  assert.equal(messages.length, before);
  run('el.onpointercancel()');
});
run('aha.overlay({});aha.center({lon:127,lat:37.5,zoom:16})');
clickCenter();
scenario(() => {
  assert.equal(messages.at(-1).type, 'pick');
  assert.ok(Math.abs(messages.at(-1).lon - 127) < 1e-10);
  assert.ok(Math.abs(messages.at(-1).lat - 37.5) < 1e-10);
});
run("aha.overlay({parcels:[{id:'parcel-a',polygons:[[[[0,0],[5,0],[5,5],[0,5]],[[1,1],[3,1],[3,3],[1,3]]]]}],bounds:[0,0,5,5]});aha.center({lon:4,lat:4});");
clickCenter();
scenario(() => { assert.equal(messages.at(-1).type, 'remove'); assert.equal(messages.at(-1).id, 'parcel-a'); });
run('aha.center({lon:2,lat:2})');
clickCenter();
scenario(() => { assert.equal(messages.at(-1).type, 'pick'); });
scenario(() => {
  assert.deepEqual([...new Set(messages.map(message => message.type))].sort(), ['pick','ready','remove','tiles']);
  assert.ok(messages.every(message => message.generation === '42'));
});
check("svg.children.some(p=>p.attributes.class==='parcel-inner'&&p.attributes['fill-rule']==='evenodd'&&(p.attributes.d.match(/Z/g)||[]).length===2)");

// A rejected Near extent stays visible; turning Near off removes only its preview.
run("aha.overlay({rings:[[[0,0],[2,0],[2,2],[0,2]]],nearRings:[[[-1,-1],[3,-1],[3,3],[-1,3]]],nearValid:false,bounds:[0,0,2,2]})");
check("svg.children.some(p=>p.attributes.class==='near-invalid'&&!p.attributes['clip-path'])&&document.getElementById('legend').textContent.includes('빨강')");
run('aha.overlay({rings:[[[0,0],[2,0],[2,2],[0,2]]],nearRings:[],bounds:[0,0,2,2]})');
check("!svg.children.some(p=>p.attributes.class==='near'||p.attributes.class==='near-invalid')&&svg.children.some(p=>p.attributes.class==='parcel')&&svg.children.some(p=>p.attributes.class==='extent')&&!document.getElementById('legend').textContent.includes('근접')");

run('aha.center({lon:127,lat:37.5,zoom:16})');
const beforeDrag = messages.length;
run('el.onpointerdown({button:0,target:el,pointerId:1,clientX:340,clientY:150});el.onpointermove({clientX:380,clientY:170})');
check("tiles.style.transform==='translate(40px,20px)'&&svg.style.transform===tiles.style.transform");
run('el.onpointerup({button:0,clientX:380,clientY:170})');
check("drag===null&&tiles.style.transform===''&&svg.style.transform===''&&lon<127&&lat>37.5");
scenario(() => { assert.equal(messages.length, beforeDrag + 1); assert.equal(messages.at(-1).type, 'tiles'); });
const beforeCancel = messages.length;
run('el.onpointerdown({button:0,target:el,pointerId:1,clientX:340,clientY:150});el.onpointermove({clientX:380,clientY:170});el.onpointercancel()');
check("drag===null&&tiles.style.transform===''&&svg.style.transform===''");
scenario(() => { assert.equal(messages.length, beforeCancel); });

run('aha.center({lon:127,lat:37.5,zoom:19});zoom(1)');
check('z===19');
run('aha.center({lon:127,lat:37.5,zoom:7});zoom(-1)');
check('z===7');
run('aha.fit({bounds:[126.997,37.497,127.003,37.503]})');
check('Math.abs(position(126.997,37.497)[0]-position(127.003,37.503)[0])<el.clientWidth*.72&&Math.abs(position(126.997,37.497)[1]-position(127.003,37.503)[1])<el.clientHeight*.65');
scenario(() => { assert.match(html, /#overlay\{width:100%;height:100%\}/); });

function wheel(deltaY, options = {}) {
  const { elapsed = 300, target = get('map'), ...input } = options;
  clock += elapsed;
  const event = { deltaY, deltaMode: 0, clientX: 340, clientY: 150,
    defaultPrevented: false, preventDefault() { this.defaultPrevented = true; }, ...input };
  target.dispatch('wheel', event);
  return event;
}
scenario(() => {
  assert.equal(get('map').listeners.get('wheel').options.passive, false);
  const before = messages.length, outside = new Element();
  assert.equal(wheel(-120, { target: outside }).defaultPrevented, false);
  assert.equal(messages.length, before); // Outer panel scroll has no map handler.
});
scenario(() => {
  run('aha.center({lon:127,lat:37.5,zoom:16})');
  const map = get('map'); map.left = 120; map.top = 75;
  const anchor = run('(()=>{const c=world(lon,lat);return geo(c[0]+510-el.clientWidth/2,c[1]+70-el.clientHeight/2)})()');
  const before = messages.length;
  assert.equal(wheel(-120, { clientX: 630, clientY: 145 }).defaultPrevented, true);
  assert.equal(run('z'), 17);
  const point = run(`position(${anchor[0]},${anchor[1]})`);
  assert.ok(Math.abs(point[0] - 510) < 1e-7 && Math.abs(point[1] - 70) < 1e-7);
  assert.equal(messages.length, before + 1);
  assert.equal(messages.at(-1).type, 'tiles');
  assert.ok(messages.at(-1).tiles.every(t => t.z === 17));
  wheel(120, { clientX: 630, clientY: 145 });
  assert.equal(run('z'), 16);
  assert.ok(Math.abs(run('lon') - 127) < 1e-10 && Math.abs(run('lat') - 37.5) < 1e-10);
  map.left = map.top = 0;
});
scenario(() => {
  run('aha.center({lon:127,lat:37.5,zoom:16})');
  const before = messages.length;
  wheel(-20); wheel(-20, { elapsed: 10 });
  assert.equal(run('z'), 16);
  wheel(-20, { elapsed: 10 });
  assert.equal(run('z'), 17);
  assert.equal(messages.length, before + 1); // Smooth trackpad pixels accumulate.
});
scenario(() => {
  run('aha.center({lon:127,lat:37.5,zoom:16})');
  const before = messages.length;
  wheel(-10000);
  for (let i = 0; i < 40; i++) wheel(-10000, { elapsed: 2 });
  assert.equal(run('z'), 17);
  assert.equal(messages.length, before + 1); // A burst cannot fan out API tile requests.
  wheel(-60, { elapsed: 80 });
  assert.equal(run('z'), 18);
  assert.equal(messages.length, before + 2);
});
scenario(() => {
  run('aha.center({lon:127,lat:37.5,zoom:16})');
  wheel(-40); wheel(-30); // Separate small gestures do not reuse stale delta.
  assert.equal(run('z'), 16);
  wheel(30, { elapsed: 10 });
  assert.equal(run('z'), 16);
  wheel(30, { elapsed: 10 });
  assert.equal(run('z'), 15); // Direction change discards the opposite remainder.
});
scenario(() => {
  run('aha.center({lon:127,lat:37.5,zoom:16})');
  wheel(-4, { deltaMode: 1 });
  assert.equal(run('z'), 17); // Line-mode mouse wheel.
  wheel(1, { deltaMode: 2 });
  assert.equal(run('z'), 16); // Page-mode device.
});
scenario(() => {
  run('aha.center({lon:127,lat:37.5,zoom:19})');
  let before = messages.length;
  assert.equal(wheel(-1000).defaultPrevented, true);
  assert.equal(run('z'), 19); assert.equal(messages.length, before);
  run('aha.center({lon:127,lat:37.5,zoom:7})'); before = messages.length;
  assert.equal(wheel(1000).defaultPrevented, true);
  assert.equal(run('z'), 7); assert.equal(messages.length, before);
});
scenario(() => {
  run('aha.center({lon:127,lat:37.5,zoom:16})');
  const before = messages.length;
  for (const delta of [0, NaN, Infinity]) assert.equal(wheel(delta).defaultPrevented, false);
  assert.equal(messages.length, before); assert.equal(run('z'), 16);
});
scenario(() => {
  run('aha.center({lon:127,lat:37.5,zoom:16});el.onpointerdown({button:0,target:el,pointerId:1,clientX:340,clientY:150})');
  const before = messages.length;
  assert.equal(wheel(-120).defaultPrevented, true);
  assert.equal(run('z'), 16); assert.equal(messages.length, before);
  run('el.onpointercancel()');
});
scenario(() => {
  run('aha.center({lon:127,lat:37.5,zoom:16});aha.overlay({rings:[[[127,37.5],[127.001,37.5],[127,37.501]]],bounds:null})');
  const state = run('JSON.stringify([lon,lat,z,rings])'), before = messages.length;
  run('window.onresize();aha.refresh();window.onresize()');
  for (const action of timers.values()) action(); timers.clear();
  assert.equal(run('JSON.stringify([lon,lat,z,rings])'), state);
  assert.equal(messages.length, before + 1);
  assert.equal(messages.at(-1).type, 'tiles');
  assert.ok(run('tiles.children.length>0 && svg.children.some(p=>p.attributes.class==="parcel")'));
});
console.log(JSON.stringify({ passed: checks, scope: 'TopoMap API picking/removal, primary-button isolation, Near preview, tile messaging, dragging, fitted extent, cursor-anchored wheel zoom, trackpad rate bounds and scroll isolation' }));
