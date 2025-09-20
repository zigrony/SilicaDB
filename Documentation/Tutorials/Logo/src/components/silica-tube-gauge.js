import '../utils/index.js';
import './silica-tube-renderer.js';
import './silica-tube-needle.js';
import './silica-tube-readout.js';

const template = document.createElement('template');
template.innerHTML = `
  <style>
    :host{display:block; width:100%; height:100%; max-width:560px; max-height:560px;}
    .svgWrapper{width:100%;height:100%;display:flex;align-items:center;justify-content:center;transform-style:preserve-3d;perspective:900px}
  </style>
  <div class="svgWrapper">
    <silica-tube-renderer id="renderer"></silica-tube-renderer>
  </div>
`;

class SilicaTubeGauge extends HTMLElement {
  static get observedAttributes(){ return ['value','min','max','smoothing','steps','depth','band','rot-x','rot-y','rot-z','zoom','green-end','yellow-end']; }

  constructor(){
    super();
    this.attachShadow({mode:'open'});
    this.shadowRoot.appendChild(template.content.cloneNode(true));
    this.renderer = this.shadowRoot.getElementById('renderer');
    this._simT = 0;
    this._simRunning = true;
    this._bindRendererEvents();
  }

  connectedCallback(){
    // propagate initial attributes to renderer
    this._syncAttributesToRenderer();
    this.start();
    this.dispatchEvent(new CustomEvent('gauge-ready'));
  }

  disconnectedCallback(){
    this.pause();
  }

  attributeChangedCallback(name, oldV, newV){
    const num = Number(newV);
    if(!isNaN(num)) this.renderer.setState({ [this._toPropName(name)]: num });
    if(name === 'value') this.renderer.setState({ value: Number(newV) });
  }

  // public API
  get value(){ return Number(this.getAttribute('value') || this.renderer.getState().value); }
  set value(v){ this.setAttribute('value', String(Number(v))); }
  get min(){ return Number(this.getAttribute('min') || this.renderer.getState().min); }
  get max(){ return Number(this.getAttribute('max') || this.renderer.getState().max); }

  setValue(v, {animate=true} = {}){
    if(animate){ this.renderer.setState({ value: Number(v) }); }
    else { this.renderer.setState({ value: Number(v) }); this.renderer.display.value = Number(v); this.renderer.renderOnce(); }
  }
  setRange(min,max){ this.setAttribute('min', String(min)); this.setAttribute('max', String(max)); this.renderer.setState({ min, max }); this.renderer.renderOnce(); }
  pulse(amount = 1){ this.renderer.setState({ value: clamp(this.renderer.getState().value + amount, this.min, this.max) }); }
  reset(){ this.setAttribute('min','-20'); this.setAttribute('max','100'); this.setAttribute('value','34'); this.renderer.setState({ smoothing:0.18, band:18, depth:12, steps:28, rotX:18, rotY:-12, rotZ:0, zoom:1, greenEnd:60, yellowEnd:80 }); this.renderer.renderOnce(); }
  start(){ this._simRunning = true; this.renderer._running = true; this.renderer._startLoop(); }
  pause(){ this._simRunning = false; this.renderer._running = false; this.renderer._stopLoop(); }

  async exportSVG(){
    return this.renderer.exportSVGString();
  }

  async exportPNG(width = 1200, height = 800){
    const s = this.renderer.exportSVGString();
    const img = new Image();
    const svg64 = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(s);
    return await new Promise((resolve, reject)=>{
      img.onload = function(){
        const canvas = document.createElement('canvas');
        canvas.width = width; canvas.height = height;
        const ctx = canvas.getContext('2d');
        ctx.fillStyle = '#031022';
        ctx.fillRect(0,0,width,height);
        ctx.drawImage(img,0,0,width,height);
        canvas.toBlob(function(blob){ resolve(blob); }, 'image/png');
      };
      img.onerror = () => reject(new Error('SVG rasterize failed'));
      img.src = svg64;
    });
  }

  _toPropName(attr){
    return attr.replace(/-([a-z])/g, (_,c)=>c.toUpperCase());
  }

  _syncAttributesToRenderer(){
    const keys = ['value','min','max','smoothing','steps','depth','band','rot-x','rot-y','rot-z','zoom','green-end','yellow-end'];
    const state = {};
    keys.forEach(k=>{
      const v = this.getAttribute(k);
      if(v !== null) state[this._toPropName(k)] = Number(v);
    });
    this.renderer.setState(state);
  }

  _bindRendererEvents(){
    this.renderer.addEventListener('renderer-input', (e)=>{
      const v = e.detail.value;
      const zone = this._zoneForValue(v);
      this.dispatchEvent(new CustomEvent('gauge-input', { detail: { value: v, zone } }));
    });
  }

  _zoneForValue(v){
    const s = this.renderer.getState();
    if(v <= s.greenEnd) return 'GREEN';
    if(v <= s.yellowEnd) return 'YELLOW';
    return 'RED';
  }
}

customElements.define('silica-tube-gauge', SilicaTubeGauge);

// local clamp (re-used)
function clamp(v,a,b){ return Math.max(a, Math.min(b, v)); }
