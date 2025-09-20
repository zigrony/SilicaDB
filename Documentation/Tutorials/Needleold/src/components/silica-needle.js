// src/components/silica-needle.js
const SVG_NS = 'http://www.w3.org/2000/svg';

function toRad(d){ return d * Math.PI / 180; }
function clamp(v,a,b){ return Math.max(a, Math.min(b, v)); }
function lerp(a,b,t){ return a + (b - a) * t; }
function valueToAngleDeg(v,min,max,start=135,end=45){
  if(max===min) return (start+end)/2;
  const t = clamp((v-min)/(max-min),0,1);
  let a0=start, a1=end;
  if(a1<=a0) a1+=360;
  return a0 + (a1-a0)*t;
}
function normalize(v){ const s = Math.hypot(...v)||1; return [v[0]/s,v[1]/s,v[2]/s]; }
function dot(a,b){ return a[0]*b[0]+a[1]*b[1]+a[2]*b[2]; }
function mixHex(a,b,t){
  const pa = parseInt(a.slice(1),16), pb = parseInt(b.slice(1),16);
  const ar=(pa>>16)&255, ag=(pa>>8)&255, ab=pa&255;
  const br=(pb>>16)&255, bg=(pb>>8)&255, bb=pb&255;
  const r=Math.round(ar + (br-ar)*t), g=Math.round(ag + (bg-ag)*t), bl=Math.round(ab + (bb-ab)*t);
  return '#' + [r,g,bl].map(x=>x.toString(16).padStart(2,'0')).join('');
}
function shadeColor(baseHex, normal, lightDir, viewDir=[0,0,1], shininess=40, F0=0.04){
  const n = normalize(normal), L = normalize(lightDir), V = normalize(viewDir);
  const diffuse = Math.max(0, dot(n,L));
  const H = normalize([L[0]+V[0], L[1]+V[1], L[2]+V[2]]);
  const spec = Math.pow(Math.max(0, dot(n,H)), shininess);
  const VdotN = Math.max(0, dot(V,n));
  const fresnel = F0 + (1 - F0) * Math.pow(1 - VdotN, 5);
  const lit = Math.min(1, 0.06 + 0.95 * diffuse);
  const mid = mixHex('#ffffff', baseHex, 0.85);
  const diffuseColor = mixHex(baseHex, mid, lit*0.36);
  const specColor = mixHex(diffuseColor, '#ffffff', spec * fresnel * 0.86);
  return specColor;
}

class SilicaNeedle extends HTMLElement {
  static get observedAttributes(){
    return ['value','min','max','cx','cy','length','width','depth','steps','color','accent','smoothing','rot-x','rot-y','rot-z','zoom','light-dir-x','light-dir-y','light-dir-z'];
  }

  constructor(){
    super();
    this.attachShadow({mode:'open'});
    this.shadowRoot.innerHTML = `
      <style>
        :host{display:block;width:100%;height:100%}
        .wrap{width:100%;height:100%;display:flex;align-items:center;justify-content:center;transform-style:preserve-3d;perspective:900px}
        svg{width:100%;height:100%;overflow:visible;display:block}
      </style>
      <div class="wrap" id="wrap">
        <svg viewBox="-220 -120 440 260" preserveAspectRatio="xMidYMid meet" role="img" aria-label="needle">
          <defs>
            <radialGradient id="gGloss" cx="40%" cy="30%" r="70%">
              <stop offset="0%" stop-color="#fff" stop-opacity="0.95"/>
              <stop offset="45%" stop-color="#fff" stop-opacity="0.22"/>
              <stop offset="100%" stop-color="#fff" stop-opacity="0"/>
            </radialGradient>
            <linearGradient id="rimGrad" x1="0" x2="1"><stop offset="0%" stop-color="rgba(255,255,255,0.12)"/><stop offset="100%" stop-color="rgba(0,0,0,0.12)"/></linearGradient>
            <filter id="smallShadow" x="-50%" y="-50%" width="200%" height="200%"><feDropShadow dx="0" dy="4" stdDeviation="6" flood-color="#000" flood-opacity="0.18"/></filter>
          </defs>
          <g id="extrude"></g>
          <g id="shape"></g>
          <g id="pivot"></g>
        </svg>
      </div>
    `;

    this._wrap = this.shadowRoot.getElementById('wrap');
    this._svg = this.shadowRoot.querySelector('svg');
    this._extrude = this.shadowRoot.getElementById('extrude');
    this._shape = this.shadowRoot.getElementById('shape');
    this._pivot = this.shadowRoot.getElementById('pivot');

    // defaults
    this._state = {
      min:0, max:100, value:34,
      cx:0, cy:40, length:120, width:10,
      depth:12, steps:18,
      color:'#ffdf6b', accent:'#c57c00',
      smoothing:0.18,
      rotX:6, rotY:-12, rotZ:0, zoom:1,
      lightDir:[0.55,-0.6,0.6]
    };

    this._display = { value: this._state.value };
    this._raf = null;
    this._lastTs = performance.now();
    this._running = true;

    this._rebuild();
    this._applyWrapperTransform();
  }

  connectedCallback(){ this._startLoop(); }
  disconnectedCallback(){ this._stopLoop(); }

  attributeChangedCallback(name, oldV, newV){
    if(newV === null) return;
    const prop = name.replace(/-([a-z])/g, (_,c)=>c.toUpperCase());
    if(prop.startsWith('lightDir')) {
      // allow setting individual light components optionally via attributes
      const x = Number(this.getAttribute('light-dir-x'));
      const y = Number(this.getAttribute('light-dir-y'));
      const z = Number(this.getAttribute('light-dir-z'));
      if(!Number.isNaN(x) && !Number.isNaN(y) && !Number.isNaN(z)) this._state.lightDir = [x,y,z];
    } else {
      const n = Number(newV);
      this._state[prop] = isNaN(n) ? newV : n;
    }
    if(['rotX','rotY','rotZ','zoom'].includes(prop)) this._applyWrapperTransform();
    this._rebuild();
  }

  // API
  setValue(v,{animate=true}={}){ this._state.value = clamp(Number(v), this._state.min, this._state.max); if(!animate){ this._display.value = this._state.value; this._rebuild(); } }
  setAnchor(x,y){ this._state.cx = Number(x); this._state.cy = Number(y); this._rebuild(); }
  setRotation({rotX,rotY,rotZ,zoom}={}){ if(rotX!==undefined) this._state.rotX=rotX; if(rotY!==undefined) this._state.rotY=rotY; if(rotZ!==undefined) this._state.rotZ=rotZ; if(zoom!==undefined) this._state.zoom=zoom; this._applyWrapperTransform(); this._rebuild(); }
  exportSVG(){ const clone = this._svg.cloneNode(true); clone.setAttribute('xmlns', SVG_NS); return Promise.resolve(new XMLSerializer().serializeToString(clone)); }

  // loop
  _startLoop(){ if(this._raf) return; this._raf = requestAnimationFrame(this._frame.bind(this)); }
  _stopLoop(){ if(this._raf){ cancelAnimationFrame(this._raf); this._raf = null; } }

  _frame(ts){
    const dt = Math.min(0.1, (ts - this._lastTs) / 1000); this._lastTs = ts;
    if(this._running){
      const alpha = Number(this._state.smoothing)||0.18;
      const tgt = this._state.value;
      this._display.value = this._display.value + (tgt - this._display.value) * alpha;
      this._display.value = clamp(this._display.value, this._state.min, this._state.max);
      this._rebuild();
      const angle = valueToAngleDeg(this._display.value, this._state.min, this._state.max) + (this._state.rotationOffset||0);
      this.dispatchEvent(new CustomEvent('needle-input',{detail:{value:this._display.value, angle}}));
    }
    this._raf = requestAnimationFrame(this._frame.bind(this));
  }

  _applyWrapperTransform(){
    const rx = Number(this._state.rotX)||0, ry = Number(this._state.rotY)||0, rz = Number(this._state.rotZ)||0, z = Number(this._state.zoom)||1;
    this._wrap.style.transform = `perspective(900px) rotateX(${rx}deg) rotateY(${ry}deg) rotateZ(${rz}deg) scale(${z})`;
  }

  // main builder: shape-first, then extrude layers shaded by rotated light
  _rebuild(){
    while(this._extrude.firstChild) this._extrude.removeChild(this._extrude.firstChild);
    while(this._shape.firstChild) this._shape.removeChild(this._shape.firstChild);
    while(this._pivot.firstChild) this._pivot.removeChild(this._pivot.firstChild);

    const s = this._state;
    const ang = valueToAngleDeg(this._display.value||s.value, s.min, s.max);
    const a = toRad(ang + (s.rotationOffset||0));
    const cx = Number(s.cx), cy = Number(s.cy);
    const len = Number(s.length), halfW = Number(s.width)/2;
    const tipX = cx + Math.cos(a)*len, tipY = cy + Math.sin(a)*len;

    // tapered hull: outer points and inner cut for bevel
    const leftX = cx + Math.cos(a + Math.PI/2)*halfW, leftY = cy + Math.sin(a + Math.PI/2)*halfW;
    const rightX = cx + Math.cos(a - Math.PI/2)*halfW, rightY = cy + Math.sin(a - Math.PI/2)*halfW;
    const inPct = 0.16;
    const innerX = cx + Math.cos(a)*(len * inPct), innerY = cy + Math.sin(a)*(len * inPct);
    const innerLeftX = innerX + Math.cos(a + Math.PI/2)*(halfW * 0.34), innerLeftY = innerY + Math.sin(a + Math.PI/2)*(halfW * 0.34);
    const innerRightX = innerX + Math.cos(a - Math.PI/2)*(halfW * 0.34), innerRightY = innerY + Math.sin(a - Math.PI/2)*(halfW * 0.34);

    // smooth tapered path using quadratic arcs for a soft bevel feel
    const shaftD = [
      `M ${leftX.toFixed(2)} ${leftY.toFixed(2)}`,
      `Q ${ (cx + leftX)/2 .toFixed ? ((cx+leftX)/2).toFixed(2) : ((cx+leftX)/2) } ${ ((cy+leftY)/2).toFixed ? ((cy+leftY)/2).toFixed(2) : ((cy+leftY)/2) } ${innerLeftX.toFixed(2)} ${innerLeftY.toFixed(2)}`,
      `L ${tipX.toFixed(2)} ${tipY.toFixed(2)}`,
      `L ${innerRightX.toFixed(2)} ${innerRightY.toFixed(2)}`,
      `Q ${ ((cx+rightX)/2).toFixed(2) } ${ ((cy+rightY)/2).toFixed(2) } ${rightX.toFixed(2)} ${rightY.toFixed(2)}`,
      'Z'
    ].join(' ');

    // compute rotated light direction so highlights move with rotation
    let light = Array.isArray(s.lightDir) ? s.lightDir.slice() : [0.55, -0.6, 0.6];
    const rx = toRad(Number(s.rotX)||0), ry = toRad(Number(s.rotY)||0);
    function rotateX(v,angle){ const c=Math.cos(angle), si=Math.sin(angle); return [v[0], v[1]*c - v[2]*si, v[1]*si + v[2]*c]; }
    function rotateY(v,angle){ const c=Math.cos(angle), si=Math.sin(angle); return [v[0]*c + v[2]*si, v[1], -v[0]*si + v[2]*c]; }
    light = rotateY( rotateX(light, -rx), -ry );

    // extruded layers with per-layer normals and shading
    const layers = Math.max(2, Math.min(60, Number(s.steps))); // cap layers to avoid heavy work
    const depthPx = Number(s.depth);
    const perLayer = (depthPx / Math.max(1, layers)) * 0.6;
    const baseColor = s.color || '#ffdf6b';

    for(let i = layers-1; i >= 0; i--){
      const ox = (i+1) * -perLayer, oy = (i+1) * perLayer * 0.5;
      const normalZ = 0.12 + (i / layers) * 0.30;
      const normal = [Math.cos(a), Math.sin(a), normalZ];
      const fill = shadeColor(baseColor, normal, light);
      const p = document.createElementNS(SVG_NS,'path');
      p.setAttribute('d', shaftD);
      p.setAttribute('transform', `translate(${ox.toFixed(2)} ${oy.toFixed(2)})`);
      p.setAttribute('fill', fill);
      p.setAttribute('stroke', 'none');
      this._extrude.appendChild(p);
    }

    // main face (rim + gloss)
    const face = document.createElementNS(SVG_NS,'path');
    face.setAttribute('d', shaftD);
    face.setAttribute('fill', baseColor);
    face.setAttribute('stroke', s.accent || '#a15b00');
    face.setAttribute('stroke-width', '0.9');
    face.setAttribute('shape-rendering', 'geometricPrecision');
    face.setAttribute('filter', 'url(#smallShadow)');
    this._shape.appendChild(face);

    // glossy overlay (view-dependent opacity)
    const gloss = document.createElementNS(SVG_NS,'path');
    gloss.setAttribute('d', shaftD);
    gloss.setAttribute('fill', 'url(#gGloss)');
    const Vdot = Math.max(0, Math.cos(rx) * Math.cos(ry));
    gloss.setAttribute('opacity', (0.06 + 0.32 * Vdot).toFixed(3));
    gloss.setAttribute('pointer-events', 'none');
    this._shape.appendChild(gloss);

    // central highlight spine
    const innerXpt = innerX, innerYpt = innerY;
    const spine = document.createElementNS(SVG_NS,'line');
    spine.setAttribute('x1', innerXpt.toFixed(2));
    spine.setAttribute('y1', innerYpt.toFixed(2));
    spine.setAttribute('x2', tipX.toFixed(2));
    spine.setAttribute('y2', tipY.toFixed(2));
    spine.setAttribute('stroke','rgba(255,255,255,0.24)');
    spine.setAttribute('stroke-width','1');
    spine.setAttribute('stroke-linecap','round');
    spine.setAttribute('pointer-events','none');
    this._shape.appendChild(spine);

    // counterweight: shadow (in extrude) + disc (in face)
    const cwR = Math.max(7, halfW * 1.1);
    const shadow = document.createElementNS(SVG_NS,'circle');
    shadow.setAttribute('cx', cx);
    shadow.setAttribute('cy', cy);
    shadow.setAttribute('r', String(cwR + 1.6));
    shadow.setAttribute('fill', 'rgba(0,0,0,0.16)');
    shadow.setAttribute('transform', `translate(${(-perLayer).toFixed(2)} ${(perLayer*0.5).toFixed(2)})`);
    this._extrude.appendChild(shadow);

    const cw = document.createElementNS(SVG_NS,'circle');
    cw.setAttribute('cx', String(cx));
    cw.setAttribute('cy', String(cy));
    cw.setAttribute('r', String(cwR));
    cw.setAttribute('fill', s.accent || '#c57c00');
    cw.setAttribute('stroke', 'rgba(0,0,0,0.16)');
    cw.setAttribute('stroke-width', '0.9');
    this._shape.appendChild(cw);

    // pivot cap and rim
    const capShadow = document.createElementNS(SVG_NS,'circle');
    capShadow.setAttribute('cx', String(cx)); capShadow.setAttribute('cy', String(cy)); capShadow.setAttribute('r', '10');
    capShadow.setAttribute('fill','rgba(0,0,0,0.12)');
    this._extrude.appendChild(capShadow);

    const cap = document.createElementNS(SVG_NS,'circle');
    cap.setAttribute('cx', String(cx)); cap.setAttribute('cy', String(cy)); cap.setAttribute('r', '6');
    cap.setAttribute('fill','#e8ebef'); cap.setAttribute('stroke','rgba(0,0,0,0.12)'); cap.setAttribute('stroke-width','0.6');
    this._shape.appendChild(cap);

    // silhouette stroke dependent on view grazing
    const viewAngleFactor = Math.abs(Math.cos(rx)) * Math.abs(Math.cos(ry));
    const sil = document.createElementNS(SVG_NS,'path');
    sil.setAttribute('d', shaftD);
    sil.setAttribute('fill','none');
    sil.setAttribute('stroke', `rgba(0,0,0,${(0.18 + 0.6*(1 - viewAngleFactor)).toFixed(3)})`);
    sil.setAttribute('stroke-width','0.95');
    this._shape.appendChild(sil);
  }
}

customElements.define('silica-needle', SilicaNeedle);
