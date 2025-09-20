import {
  toRad, clamp, normalize,
  valueToAngleDeg, buildRibbonPathPoints,
  ptsToPathFromPairs, shadeHex, averageHexColors
} from '../utils/index.js';

const SVG_NS = 'http://www.w3.org/2000/svg';

export class SilicaTubeRenderer extends HTMLElement {
  constructor(){
    super();
    this.attachShadow({mode:'open'});
    this.svg = this._buildSVG();
    this.shadowRoot.appendChild(this.svg);
    this.zonesG = this.svg.querySelector('#zones');
    this.ticksG = this.svg.querySelector('#ticks');
    this.needlePath = this.svg.querySelector('#needle');
    this.needleGroup = this.svg.querySelector('#needleGroup');
    this.valueText = this.svg.querySelector('#valueText');
    this.unitText = this.svg.querySelector('#unitText');
    this.minLabel = this.svg.querySelector('#minLabel');
    this.maxLabel = this.svg.querySelector('#maxLabel');

    // internal state
    this._renderState = Object.assign({
      min: -20, max: 100, value: 34, smoothing: 0.18,
      band: 18, depth: 12, steps: 28,
      rotX: 18, rotY: -12, rotZ: 0, zoom: 1,
      greenEnd: 60, yellowEnd: 80
    }, this._parseAttributes());

    this.display = { value: this._renderState.value };
    this._raf = null;
    this._lastTs = performance.now();
    this._running = true;
  }

  connectedCallback(){
    this._startLoop();
    this._attachInteraction();
  }

  disconnectedCallback(){
    this._stopLoop();
    this._detachInteraction();
  }

  setState(p){
    Object.assign(this._renderState, p);
  }

  getState(){
    return Object.assign({}, this._renderState, { value: this.display.value });
  }

  // Public small API for the top-level to call
  renderOnce(){
    this._buildTicks();
    this._drawTube();
    this._drawNeedle();
  }

  exportSVGString(){
    const clone = this.svg.cloneNode(true);
    clone.setAttribute('xmlns', SVG_NS);
    return new XMLSerializer().serializeToString(clone);
  }

  // -- internal rendering loop
  _startLoop(){
    if(this._raf) return;
    this._raf = requestAnimationFrame(this._frame.bind(this));
  }
  _stopLoop(){
    if(this._raf){ cancelAnimationFrame(this._raf); this._raf = null; }
  }

  _frame(ts){
    const dt = Math.min(0.1, (ts - this._lastTs) / 1000);
    this._lastTs = ts;
    if(this._running){
      // smoothing
      const alpha = this._renderState.smoothing;
      const tgt = this._renderState.value;
      this.display.value = this.display.value + (tgt - this.display.value) * alpha;
      this.display.value = clamp(this.display.value, this._renderState.min, this._renderState.max);
      this._buildTicks();
      this._drawTube();
      this._drawNeedle();
      // update readout
      this.valueText.textContent = (this.display.value).toFixed(1);
      this.unitText.textContent = `range ${this._renderState.min}..${this._renderState.max}`;
      this.minLabel.textContent = String(this._renderState.min);
      this.maxLabel.textContent = String(this._renderState.max);
      // dispatch small event
      this.dispatchEvent(new CustomEvent('renderer-input', {detail: {value: this.display.value}}));
    }
    this._raf = requestAnimationFrame(this._frame.bind(this));
  }

  // build ticks (keeps same behavior as original)
  _buildTicks(){
    while(this.ticksG.firstChild) this.ticksG.removeChild(this.ticksG.firstChild);
    const minVal = this._renderState.min, maxVal = this._renderState.max;
    const ticks = 20;
    for(let i=0;i<=ticks;i++){
      const v = minVal + (maxVal - minVal) * i / ticks;
      const deg = valueToAngleDeg(v, minVal, maxVal);
      const a = toRad(deg);
      const rOut = 150 + (i%5===0 ? 12 : 6);
      const rIn = 150 - 12;
      const x1 = Math.cos(a) * rIn, y1 = Math.sin(a) * rIn;
      const x2 = Math.cos(a) * rOut, y2 = Math.sin(a) * rOut;
      const line = document.createElementNS(SVG_NS,'line');
      line.setAttribute('x1', x1); line.setAttribute('y1', y1); line.setAttribute('x2', x2); line.setAttribute('y2', y2);
      line.setAttribute('stroke', '#e6eef8');
      line.setAttribute('stroke-width', i%5===0 ? 2.2 : 1.2);
      line.setAttribute('stroke-linecap', 'square');
      this.ticksG.appendChild(line);
      if(i%5===0){
        const tx = Math.cos(a) * (150 - 30), ty = Math.sin(a) * (150 - 30);
        const lbl = document.createElementNS(SVG_NS,'text');
        lbl.setAttribute('x', String(tx)); lbl.setAttribute('y', String(ty + 4));
        lbl.setAttribute('fill', '#cfe3ff'); lbl.setAttribute('font-size', '12'); lbl.setAttribute('text-anchor', 'middle');
        lbl.textContent = Number.isInteger(v) ? String(Math.round(v)) : v.toFixed(1);
        this.ticksG.appendChild(lbl);
      }
    }
  }

  _drawTube(){
    while(this.zonesG.firstChild) this.zonesG.removeChild(this.zonesG.firstChild);

    const s = this._renderState;
    const minVal = s.min, maxVal = s.max;
    const gEnd = clamp(s.greenEnd, minVal, maxVal);
    const yEnd = clamp(s.yellowEnd, minVal, maxVal);
    const bandThickness = s.band;
    const outerR = 150;
    const innerR = Math.max(6, outerR - bandThickness);
    const depthPx = s.depth;
    const layers = Math.max(2, Math.min(120, s.steps));
    const pitch = toRad(clamp(Number(s.rotX), -85, 85));
    const yaw = toRad(Number(s.rotY));
    const viewFacing = Math.max(0.08, Math.abs(Math.cos(pitch) * Math.cos(yaw)));
    const camX = Math.sin(toRad(Number(s.rotY)));
    const camY = -Math.sin(toRad(Number(s.rotX)));
    const perLayerCamX = (camX * depthPx / Math.max(1, layers)) * viewFacing;
    const perLayerCamY = (camY * depthPx / Math.max(1, layers)) * viewFacing;
    const scaleStep = 0.004 + (depthPx/500)*0.01;
    const a0 = toRad(valueToAngleDeg(minVal, minVal, maxVal));
    const a1 = toRad(valueToAngleDeg(maxVal, minVal, maxVal));
    const ag = toRad(valueToAngleDeg(gEnd, minVal, maxVal));
    const ay = toRad(valueToAngleDeg(yEnd, minVal, maxVal));
    const curvatureZBase = 0.14 + (Math.abs(pitch)/90)*0.28;

    const segments = [
      {a0:a0, a1:ag, color:'#2ecc71'},
      {a0:ag, a1:ay, color:'#f39c12'},
      {a0:ay, a1:a1, color:'#e74c3c'}
    ];

    for(let layer = layers-1; layer >= 0; layer--){
      const idx = layer + 1;
      const scale = 1 - idx * scaleStep;
      const oR = outerR * scale;
      const iR = innerR * scale;
      const camOx = idx * perLayerCamX;
      const camOy = idx * perLayerCamY;
      const radialInset = idx * (depthPx / Math.max(1, layers)) * 0.25 * viewFacing;
      const t = layer / Math.max(1, layers-1);
      const layerOpacity = 0.06 + 0.86 * (1 - t);

      for(const seg of segments){
        if(Math.abs(seg.a1 - seg.a0) < 1e-5) continue;
        const outerPtsRaw = buildRibbonPathPoints(seg.a0, seg.a1, oR, curvatureZBase);
        const innerPtsRaw = buildRibbonPathPoints(seg.a0, seg.a1, iR, curvatureZBase);

        const outerPts = outerPtsRaw.map(p=>{
          const rx = -p.nx * radialInset, ry = -p.ny * radialInset;
          const z = idx * (depthPx / Math.max(1, layers)) * viewFacing;
          const px = p.x + rx + camOx, py = p.y + ry + camOy;
          const normal = normalize([p.nx, p.ny, p.nz + 0.02]);
          const fillColor = shadeHex(seg.color, normal, 30, 0.06, 0.04);
          return { px, py, pz: z, normal, fillColor };
        });
        const innerPts = innerPtsRaw.map(p=>{
          const rx = -p.nx * radialInset, ry = -p.ny * radialInset;
          const z = idx * (depthPx / Math.max(1, layers)) * viewFacing;
          const px = p.x + rx + camOx, py = p.y + ry + camOy;
          const normal = normalize([-p.nx, -p.ny, p.nz*0.9]);
          const fillColor = shadeHex(seg.color, normal, 18, 0.04, 0.04);
          return { px, py, pz: z, normal, fillColor };
        });

        const outerPairs = outerPts.map(p=>[p.px,p.py]);
        const innerPairs = innerPts.map(p=>[p.px,p.py]);
        const d = ptsToPathFromPairs(outerPairs, innerPairs);

        const path = document.createElementNS(SVG_NS,'path');
        const avgFill = averageHexColors(outerPts.map(p=>p.fillColor).concat(innerPts.map(p=>p.fillColor)));
        path.setAttribute('d', d);
        path.setAttribute('fill', avgFill);
        path.setAttribute('opacity', String(layerOpacity));
        path.setAttribute('stroke', 'none');
        this.zonesG.appendChild(path);
      }
    }

    // top faces
    for(const seg of segments){
      if(Math.abs(seg.a1 - seg.a0) < 1e-5) continue;
      const outerPtsRaw = buildRibbonPathPoints(seg.a0, seg.a1, outerR, curvatureZBase);
      const innerPtsRaw = buildRibbonPathPoints(seg.a0, seg.a1, innerR, curvatureZBase);
      const outerPairs = outerPtsRaw.map(p=>[p.x,p.y]);
      const innerPairs = innerPtsRaw.map(p=>[p.x,p.y]);
      const d = ptsToPathFromPairs(outerPairs, innerPairs);
      const top = document.createElementNS(SVG_NS,'path');

      const midA = (seg.a0 + seg.a1)/2;
      const rimNormal = normalize([Math.cos(midA), Math.sin(midA), curvatureZBase*1.6]);
      const rimColor = shadeHex(seg.color, rimNormal, 48, 0.08, 0.04, 1.0, 1.0);
      const topNormal = normalize([Math.cos(midA), Math.sin(midA), curvatureZBase*1.1]);
      const topFill = shadeHex(seg.color, topNormal, 32, 0.04, 0.04, 1.0, 0.6);

      top.setAttribute('d', d);
      top.setAttribute('fill', topFill);
      top.setAttribute('stroke', rimColor);
      top.setAttribute('stroke-width', '0.8');
      this.zonesG.appendChild(top);
    }

    // bevels, rim, shadow, silhouette (kept minimal)
    const bevelR = 1.8;
    const outerBevelPairs = buildRibbonPathPoints(a0, a1, outerR + bevelR, curvatureZBase*0.9).map(p=>[p.x,p.y]);
    const innerBevelPairs = buildRibbonPathPoints(a0, a1, innerR - bevelR, curvatureZBase*0.9).map(p=>[p.x,p.y]);
    const bevelPath = document.createElementNS(SVG_NS,'path');
    bevelPath.setAttribute('d', ptsToPathFromPairs(outerBevelPairs, innerBevelPairs));
    bevelPath.setAttribute('fill', 'rgba(255,255,255,0.02)');
    bevelPath.setAttribute('stroke', 'rgba(0,0,0,0.22)');
    bevelPath.setAttribute('stroke-width','0.6');
    this.zonesG.appendChild(bevelPath);

    const innerHighlight = document.createElementNS(SVG_NS,'path');
    const hOuter = buildRibbonPathPoints(a0,a1,outerR-0.5, curvatureZBase).map(p=>[p.x,p.y]);
    const hInner = buildRibbonPathPoints(a0,a1,innerR+0.5, curvatureZBase).map(p=>[p.x,p.y]);
    innerHighlight.setAttribute('d', ptsToPathFromPairs(hOuter, hInner));
    innerHighlight.setAttribute('fill','none');
    innerHighlight.setAttribute('stroke','rgba(255,255,255,0.06)');
    innerHighlight.setAttribute('stroke-width','1');
    this.zonesG.appendChild(innerHighlight);
  }

  _drawNeedle(){
    const s = this._renderState;
    const rawAngle = valueToAngleDeg(this.display.value, s.min, s.max);
    let a0 = 135 % 360; if(a0 < 0) a0 += 360;
    let a1 = 45 % 360; if(a1 < 0) a1 += 360;
    if(a1 <= a0) a1 += 360;
    let a = rawAngle;
    while(a < a0 - 360) a += 360;
    while(a > a1 + 360) a -= 360;
    while(a < a0) a += 360;
    while(a > a1) a -= 360;
    if(a < a0) a = a0;
    if(a > a1) a = a1;

    const rad = toRad(a);
    const len = 150 - 28;
    const tx = Math.cos(rad) * len;
    const ty = Math.sin(rad) * len;
    const halfWidth = 8;
    const perp = Math.PI / 2;
    const lx = Math.cos(rad + perp) * halfWidth;
    const ly = Math.sin(rad + perp) * halfWidth;
    const rxp = Math.cos(rad - perp) * halfWidth;
    const ryp = Math.sin(rad - perp) * halfWidth;
    const baseInset = 12;
    const bx = Math.cos(rad) * baseInset;
    const by = Math.sin(rad) * baseInset;
    const leftX = bx + lx, leftY = by + ly;
    const rightX = bx + rxp, rightY = by + ryp;

    Array.from(this.needleGroup.querySelectorAll('.extrude')).forEach(n=>n.remove());

    const mainD = `M ${leftX.toFixed(2)} ${leftY.toFixed(2)} L ${tx.toFixed(2)} ${ty.toFixed(2)} L ${rightX.toFixed(2)} ${rightY.toFixed(2)} Z`;
    const layerCount = Math.max(1, Math.min(80, s.steps));
    const depthPx = s.depth;
    const dx = Math.sin(toRad(Number(s.rotY)));
    const dy = -Math.sin(toRad(clamp(Number(s.rotX), -85, 85)));
    const perLayerX = dx * (depthPx / Math.max(1, layerCount));
    const perLayerY = dy * (depthPx / Math.max(1, layerCount));
    for(let i = layerCount - 1; i >= 0; i--){
      const ox = (i+1) * perLayerX;
      const oy = (i+1) * perLayerY;
      const p = document.createElementNS(SVG_NS,'path');
      p.setAttribute('d', mainD);
      p.setAttribute('transform', `translate(${ox.toFixed(2)} ${oy.toFixed(2)})`);
      const normal = normalize([Math.cos(rad), Math.sin(rad), 0.18]);
      const layerFill = shadeHex('#c57c00', normal, 32, 0.06, 0.04);
      const layerStroke = shadeHex('#8f5d00', normal, 18, 0.04, 0.04);
      p.setAttribute('fill', layerFill);
      p.setAttribute('stroke', layerStroke);
      p.setAttribute('stroke-width', '0.6');
      p.setAttribute('opacity', String(0.06 + 0.72 * (1 - (i/(layerCount-1||1)))));
      p.classList.add('extrude');
      this.needleGroup.insertBefore(p, this.needlePath);
    }

    this.needlePath.setAttribute('d', mainD);
  }

  // build minimal SVG template similar to original (defs simplified)
  _buildSVG(){
    const svg = document.createElementNS(SVG_NS, 'svg');
    svg.setAttribute('viewBox','-220 -120 440 260');
    svg.setAttribute('preserveAspectRatio','xMidYMid meet');
    svg.setAttribute('role','img');
    svg.setAttribute('aria-label','Pressure gauge');
    svg.innerHTML = `
      <defs>
        <filter id="blurShadow" x="-50%" y="-50%" width="200%" height="200%">
          <feGaussianBlur stdDeviation="6" result="b"/><feOffset dy="6" result="o"/>
          <feMerge><feMergeNode in="o"/><feMergeNode in="SourceGraphic"/></feMerge>
        </filter>
        <filter id="needleShadow" x="-200%" y="-200%" width="400%" height="400%"><feDropShadow dx="0" dy="6" stdDeviation="6" flood-color="#000" flood-opacity="0.22"/></filter>
      </defs>
      <g id="zones"></g>
      <g id="ticks"></g>
      <g id="needleGroup">
        <circle id="pivotBack" cx="0" cy="40" r="10" fill="rgba(0,0,0,0.18)"></circle>
        <path id="needle" d="" fill="#ffdf6b" stroke="#c57c00" stroke-width="0.8"></path>
        <circle id="pivotCap" cx="0" cy="40" r="6" fill="#ddd"></circle>
      </g>
      <g id="readout" transform="translate(0,105)">
        <rect x="-100" y="-26" width="200" height="34" rx="8" fill="rgba(0,0,0,0.36)"/>
        <text id="valueText" x="0" y="0" fill="#e6eef8" font-size="20" text-anchor="middle" font-weight="600">0</text>
        <text id="unitText" x="0" y="18" fill="#9aa7bd" font-size="11" text-anchor="middle">units</text>
      </g>
      <text id="minLabel" x="-180" y="140" fill="#9aa7bd" font-size="12" text-anchor="middle">min</text>
      <text id="maxLabel" x="180" y="140" fill="#9aa7bd" font-size="12" text-anchor="middle">max</text>
    `;
    return svg;
  }

  _attachInteraction(){
    this._onClick = (ev)=>{
      const rect = this.svg.getBoundingClientRect();
      const x = ev.clientX - (rect.left + rect.width/2);
      const impulse = clamp(Math.abs(x)/160 * (this._renderState.max - this._renderState.min) * 0.06, 1, (this._renderState.max - this._renderState.min)*0.4);
      this._renderState.value = clamp(this._renderState.value + impulse, this._renderState.min, this._renderState.max);
    };
    this.svg.addEventListener('click', this._onClick);
  }
  _detachInteraction(){
    this.svg.removeEventListener('click', this._onClick);
    this._onClick = null;
  }

  _parseAttributes(){
    // read initial attributes if any
    const out = {};
    ['min','max','value','smoothing','band','depth','steps','rot-x','rot-y','rot-z','zoom','green-end','yellow-end'].forEach(k=>{
      const attr = this.getAttribute(k);
      if(attr !== null){
        const prop = k.replace(/-([a-z])/g, (_,c)=>c.toUpperCase());
        out[prop] = Number(attr);
      }
    });
    return out;
  }
}

customElements.define('silica-tube-renderer', SilicaTubeRenderer);
