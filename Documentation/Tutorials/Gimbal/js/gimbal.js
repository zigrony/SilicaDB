// Gimbal instrument with stable target/display separation, keyboard, ARIA, export, and telemetry API
(function(){
  const svg = document.getElementById('svg');
  const rings = document.getElementById('rings');
  const attitude = document.getElementById('attitude');
  const labels = document.getElementById('labels');

  // controls
  const mode = document.getElementById('mode');
  const manualControls = document.getElementById('manualControls');
  const pitchR = document.getElementById('pitch'), pitchn = document.getElementById('pitchn');
  const rollR = document.getElementById('roll'), rolln = document.getElementById('rolln');
  const headR = document.getElementById('heading'), headn = document.getElementById('headingn');
  const zoomR = document.getElementById('zoom'), zoomn = document.getElementById('zoomn');
  const toggleSim = document.getElementById('toggleSim');
  const mPitch = document.getElementById('mPitch'), mRoll = document.getElementById('mRoll'), mHead = document.getElementById('mHead');
  const copySvgBtn = document.getElementById('copySvg'), exportPngBtn = document.getElementById('exportPng'), compactBtn = document.getElementById('compact');

  // authoritative telemetry targets
  const state = { pitch:-10, roll:6, heading:45, zoom:1 };

  // smoothed display (render-only)
  const display = { pitch: state.pitch, roll: state.roll, heading: state.heading, zoom: state.zoom };

  // simulation
  let simT = 0, simRunning = true;

  // helpers
  const deg = Math.PI/180;
  const clamp = (v,a,b)=>Math.max(a,Math.min(b,v));
  function smooth(dst, src, alpha){ return dst + (src - dst) * Math.min(1, alpha); }

  // topology creation (rings + compass ticks)
  function createRings(){
    while(rings.firstChild) rings.removeChild(rings.firstChild);
    const bezel = document.createElementNS('http://www.w3.org/2000/svg','circle');
    bezel.setAttribute('cx','0'); bezel.setAttribute('cy','0'); bezel.setAttribute('r','140');
    bezel.setAttribute('fill','none'); bezel.setAttribute('stroke','rgba(255,255,255,0.06)'); bezel.setAttribute('stroke-width','6');
    rings.appendChild(bezel);
    for(let i=0;i<3;i++){
      const r = 95 - i*20;
      const g = document.createElementNS('http://www.w3.org/2000/svg','circle');
      g.setAttribute('cx','0'); g.setAttribute('cy','0'); g.setAttribute('r', String(r));
      g.setAttribute('fill','none'); g.setAttribute('stroke','rgba(180,200,230,0.06)'); g.setAttribute('stroke-width','2');
      rings.appendChild(g);
    }
    for(let a=0;a<360;a+=30){
      const rad = a * deg;
      const x1 = Math.cos(rad)*148, y1 = -Math.sin(rad)*148;
      const x2 = Math.cos(rad)*134, y2 = -Math.sin(rad)*134;
      const line = document.createElementNS('http://www.w3.org/2000/svg','line');
      line.setAttribute('x1', String(x1)); line.setAttribute('y1', String(y1));
      line.setAttribute('x2', String(x2)); line.setAttribute('y2', String(y2));
      line.setAttribute('stroke','rgba(200,220,255,0.08)'); line.setAttribute('stroke-width','1.5');
      rings.appendChild(line);
      const label = document.createElementNS('http://www.w3.org/2000/svg','text');
      const lab = (a===0)?'N':(a===90)?'E':(a===180)?'S':(a===270)?'W':String(a);
      label.setAttribute('x', String(Math.cos(rad)*122)); label.setAttribute('y', String(-Math.sin(rad)*122+4));
      label.setAttribute('fill','#cfe3ff'); label.setAttribute('font-size','11'); label.setAttribute('text-anchor','middle');
      label.textContent = lab;
      rings.appendChild(label);
    }
  }

  // dynamic nodes, created once and reused
  const nodes = {};
  function createDynamicNodes(){
    while(attitude.firstChild) attitude.removeChild(attitude.firstChild);
    while(labels.firstChild) labels.removeChild(labels.firstChild);

    const diskGroup = document.createElementNS('http://www.w3.org/2000/svg','g');
    diskGroup.setAttribute('id','diskGroup');
    attitude.appendChild(diskGroup);
    nodes.diskGroup = diskGroup;

    const sky = document.createElementNS('http://www.w3.org/2000/svg','rect');
    sky.setAttribute('x','-160'); sky.setAttribute('y','-160'); sky.setAttribute('width','320'); sky.setAttribute('height','320');
    sky.setAttribute('fill','#11324a'); diskGroup.appendChild(sky);
    const ground = document.createElementNS('http://www.w3.org/2000/svg','rect');
    ground.setAttribute('x','-160'); ground.setAttribute('y','-160'); ground.setAttribute('width','320'); ground.setAttribute('height','320');
    ground.setAttribute('fill','#2d1f12'); diskGroup.appendChild(ground);

    const clipId = 'diskClip';
    const clip = document.createElementNS('http://www.w3.org/2000/svg','clipPath');
    clip.setAttribute('id', clipId);
    const circ = document.createElementNS('http://www.w3.org/2000/svg','circle');
    circ.setAttribute('cx','0'); circ.setAttribute('cy','0'); circ.setAttribute('r','100');
    clip.appendChild(circ);
    let defs = svg.querySelector('defs');
    if(!defs){ defs = document.createElementNS('http://www.w3.org/2000/svg','defs'); svg.insertBefore(defs, svg.firstChild); }
    const old = defs.querySelector('#'+clipId);
    if(old) defs.removeChild(old);
    defs.appendChild(clip);
    diskGroup.setAttribute('clip-path','url(#'+clipId+')');

    const horizon = document.createElementNS('http://www.w3.org/2000/svg','line');
    horizon.setAttribute('x1','-200'); horizon.setAttribute('y1','0'); horizon.setAttribute('x2','200'); horizon.setAttribute('y2','0');
    horizon.setAttribute('stroke','#e6eef8'); horizon.setAttribute('stroke-width','1.6'); horizon.setAttribute('opacity','0.9');
    diskGroup.appendChild(horizon);
    nodes.horizon = horizon;

    const cross = document.createElementNS('http://www.w3.org/2000/svg','g');
    cross.setAttribute('id','cross');
    const ch1 = document.createElementNS('http://www.w3.org/2000/svg','line');
    ch1.setAttribute('x1','-60'); ch1.setAttribute('y1','0'); ch1.setAttribute('x2','60'); ch1.setAttribute('y2','0'); ch1.setAttribute('stroke','#cfe3ff'); ch1.setAttribute('stroke-width','1.2');
    const ch2 = document.createElementNS('http://www.w3.org/2000/svg','line');
    ch2.setAttribute('x1','0'); ch2.setAttribute('y1','-30'); ch2.setAttribute('x2','0'); ch2.setAttribute('y2','30'); ch2.setAttribute('stroke','#cfe3ff'); ch2.setAttribute('stroke-width','1.2');
    cross.appendChild(ch1); cross.appendChild(ch2);
    attitude.appendChild(cross);
    nodes.cross = cross;

    const needleGroup = document.createElementNS('http://www.w3.org/2000/svg','g');
    needleGroup.setAttribute('id','needleGroup');
    attitude.appendChild(needleGroup);
    const needle = document.createElementNS('http://www.w3.org/2000/svg','path');
    needle.setAttribute('d','M0,-146 L6,-132 L0,-120 L-6,-132 Z');
    needle.setAttribute('fill','#ffcc66'); needle.setAttribute('stroke','rgba(0,0,0,0.2)'); needle.setAttribute('stroke-width','0.6');
    needleGroup.appendChild(needle);
    nodes.needleGroup = needleGroup;

    const headingLabel = document.createElementNS('http://www.w3.org/2000/svg','text');
    headingLabel.setAttribute('x','0'); headingLabel.setAttribute('y','175'); headingLabel.setAttribute('text-anchor','middle');
    headingLabel.setAttribute('fill','#cfe3ff'); headingLabel.setAttribute('font-size','14'); headingLabel.textContent = 'HDG 000';
    labels.appendChild(headingLabel);
    nodes.headingLabel = headingLabel;
  }

  // initial build
  createRings();
  createDynamicNodes();

  // apply transforms using display values only
  function applyAttitudeTransform(){
    const tx = clamp(display.pitch * 1.6, -80, 80);
    const ry = clamp(display.roll, -45, 45);
    nodes.diskGroup.setAttribute('transform', `translate(0,${tx}) rotate(${ -ry }) scale(${display.zoom})`);
    nodes.cross.setAttribute('transform', `rotate(${ -ry }) scale(${display.zoom})`);
    nodes.needleGroup.setAttribute('transform', `rotate(${display.heading}) scale(${display.zoom})`);
    nodes.headingLabel.textContent = 'HDG ' + String(Math.round(display.heading)).padStart(3,'0');
    nodes.headingLabel.setAttribute('transform', `scale(${display.zoom})`);
  }

  // smoothing only modifies display
  function smoothDisplay(alpha = 0.16){
    display.pitch = smooth(display.pitch, state.pitch, alpha);
    display.roll = smooth(display.roll, state.roll, alpha);
    const cur = display.heading;
    const diff = (((state.heading - cur + 540) % 360) - 180);
    display.heading = (cur + diff * Math.min(1, alpha) + 360) % 360;
    display.zoom = smooth(display.zoom, state.zoom, alpha);
  }

  // UI bindings with clear separation
  function bind(range, num, field){
    range.addEventListener('input', ()=>{ num.value = range.value; state[field] = parseFloat(range.value); updateMetrics(); });
    num.addEventListener('input', ()=>{ range.value = num.value; state[field] = parseFloat(num.value); updateMetrics(); });
  }
  bind(pitchR, pitchn, 'pitch');
  bind(rollR, rolln, 'roll');
  bind(headR, headn, 'heading');
  bind(zoomR, zoomn, 'zoom');

  function updateMetrics(){
    mPitch.textContent = state.pitch.toFixed(1);
    mRoll.textContent = state.roll.toFixed(1);
    mHead.textContent = state.heading.toFixed(1);
  }

  // mode handling
  mode.addEventListener('change', ()=>{
    if(mode.value === 'manual'){ manualControls.classList.remove('hidden'); simRunning = false; toggleSim.textContent='Sim paused'; }
    else { manualControls.classList.add('hidden'); simRunning = true; toggleSim.textContent='Pause sim'; }
  });
  toggleSim.addEventListener('click', ()=>{ simRunning = !simRunning; toggleSim.textContent = simRunning ? 'Pause sim' : 'Resume sim'; });

  // click toggles crosshair
  svg.addEventListener('click', ()=>{ const cur = nodes.cross.getAttribute('display') || 'inline'; nodes.cross.setAttribute('display', cur === 'none' ? 'inline' : 'none'); });

  // simple sim
  function simulate(dt){
    simT += dt * 0.7;
    const pitch = -10 + Math.sin(simT * 0.9) * 10;
    const roll = 6 + Math.cos(simT * 0.6) * 12;
    const heading = (simT * 15) % 360;
    return { pitch, roll, heading };
  }

  // telemetry API
  window.applyTelemetry = function(payload){
    // payload fields: pitch, roll, heading, zoom (optional)
    if(typeof payload !== 'object') return;
    if('pitch' in payload) state.pitch = Number(payload.pitch);
    if('roll' in payload) state.roll = Number(payload.roll);
    if('heading' in payload) state.heading = ((Number(payload.heading) % 360) + 360) % 360;
    if('zoom' in payload) state.zoom = Number(payload.zoom);
  };

  // keyboard controls
  window.addEventListener('keydown', (e)=>{
    const nudge = e.shiftKey ? 5 : 1;
    if(e.key === 'ArrowUp'){ state.pitch = clamp(state.pitch - nudge, -45, 45); updateMetrics(); }
    if(e.key === 'ArrowDown'){ state.pitch = clamp(state.pitch + nudge, -45, 45); updateMetrics(); }
    if(e.key === 'ArrowLeft'){ state.roll = clamp(state.roll - nudge, -45, 45); updateMetrics(); }
    if(e.key === 'ArrowRight'){ state.roll = clamp(state.roll + nudge, -45, 45); updateMetrics(); }
    if(e.key === 'c'){ nodes.cross.setAttribute('display', (nodes.cross.getAttribute('display') === 'none') ? 'inline' : 'none'); }
  });

  // copy svg
  copySvgBtn.addEventListener('click', ()=>{
    const s = new XMLSerializer();
    const clone = svg.cloneNode(true);
    clone.setAttribute('xmlns','http://www.w3.org/2000/svg');
    const out = s.serializeToString(clone);
    navigator.clipboard?.writeText(out).then(()=>{ copySvgBtn.textContent='Copied'; setTimeout(()=>copySvgBtn.textContent='Copy SVG',900); }, ()=>{ copySvgBtn.textContent='Fail'; setTimeout(()=>copySvgBtn.textContent='Copy SVG',900); });
  });

  // export PNG using canvas
  exportPngBtn.addEventListener('click', ()=>{
    const s = new XMLSerializer();
    const clone = svg.cloneNode(true);
    clone.setAttribute('xmlns','http://www.w3.org/2000/svg');
    const out = s.serializeToString(clone);
    const img = new Image();
    const svg64 = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(out);
    img.onload = function(){
      const canvas = document.createElement('canvas');
      const w = img.width || 800, h = img.height || 800;
      canvas.width = w; canvas.height = h;
      const ctx = canvas.getContext('2d');
      ctx.fillStyle = '#041020';
      ctx.fillRect(0,0,w,h);
      ctx.drawImage(img,0,0,w,h);
      canvas.toBlob(function(blob){
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = 'gimbal.png';
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(url);
      });
    };
    img.src = svg64;
  });

  // compact mode toggle
  compactBtn.addEventListener('click', ()=>{
    document.body.classList.toggle('compact');
    compactBtn.textContent = document.body.classList.contains('compact') ? 'Full' : 'Compact';
  });

  // main loop
  let lastTs = performance.now();
  function loop(ts){
    const dt = Math.min(0.1, (ts - lastTs) / 1000);
    lastTs = ts;
    if(mode.value === 'sim' && simRunning){
      const raw = simulate(dt);
      state.pitch = raw.pitch; state.roll = raw.roll; state.heading = raw.heading;
      mPitch.textContent = raw.pitch.toFixed(1); mRoll.textContent = raw.roll.toFixed(1); mHead.textContent = raw.heading.toFixed(1);
    }
    // smooth display only
    display.pitch = smooth(display.pitch, state.pitch, 0.18);
    display.roll = smooth(display.roll, state.roll, 0.18);
    const diff = (((state.heading - display.heading + 540) % 360) - 180);
    display.heading = (display.heading + diff * 0.14 + 360) % 360;
    display.zoom = smooth(display.zoom, state.zoom, 0.18);

    applyAttitudeTransform();
    requestAnimationFrame(loop);
  }

  // init values
  updateMetrics();
  requestAnimationFrame(loop);

  // expose debug
  window.__gyro = { state, display };
})();
