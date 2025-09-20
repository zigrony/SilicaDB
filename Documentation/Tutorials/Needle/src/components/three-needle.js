// src/components/three-needle.js
// Web Component that renders a 3D needle using three.js into a transparent canvas.
// Imports three.js from unpkg so no local install is required for quick demo.

import * as THREE from '../../demo/vendor/three/three.module.js';
import { OrbitControls } from '../../demo/vendor/three/controls/OrbitControls.js';

const SVG_NS = 'http://www.w3.org/2000/svg';

class ThreeNeedle extends HTMLElement {
  static get observedAttributes(){ return ['value','min','max','cx','cy','length','width','depth','color','accent','smoothing','rot-x','rot-y','rot-z','zoom']; }

  constructor(){
    super();
    this.attachShadow({mode:'open'});

    const style = document.createElement('style');
    style.textContent = `:host{display:block;position:relative;width:100%;height:100%}canvas{display:block;width:100%;height:100%}`;
    this.shadowRoot.appendChild(style);

    // canvas for three.js
    this._canvas = document.createElement('canvas');
    this._canvas.style.touchAction = 'none';
    this.shadowRoot.appendChild(this._canvas);

    // defaults state
    this._state = {
      min: 0, max: 100, value: 34,
      cx: 0, cy: 40, length: 120, width: 10,
      depth: 12,
      color: '#ffdf6b', accent: '#c57c00',
      smoothing: 0.18,
      rotX: 6, rotY: -12, rotZ: 0, zoom: 1
    };

    this._display = { value: this._state.value };
    this._animating = true;

    // three.js essentials
    this._renderer = new THREE.WebGLRenderer({ canvas: this._canvas, alpha: true, antialias: true });
    this._renderer.outputEncoding = THREE.sRGBEncoding;
    this._renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));

    this._scene = new THREE.Scene();
    this._camera = new THREE.PerspectiveCamera(50, 1, 0.1, 2000);
    this._camera.position.set(0, 0, 360);

    // lights
    this._dirLight = new THREE.DirectionalLight(0xffffff, 0.9);
    this._dirLight.position.set(120, 160, 100);
    this._scene.add(this._dirLight);
    this._ambient = new THREE.AmbientLight(0xffffff, 0.45);
    this._scene.add(this._ambient);

    // optional environment reflection map small
    const pmremGen = new THREE.PMREMGenerator(this._renderer);
    pmremGen.compileEquirectangularShader();
    // small neutral env (not required) - skip heavy env texture for demo

    this._needleGroup = new THREE.Group();
    this._scene.add(this._needleGroup);

    this._buildNeedleMesh();

    // optionally expose orbit controls for debug (disabled by default)
    this._controls = new OrbitControls(this._camera, this._renderer.domElement);
    this._controls.enabled = false;

    this._boundRAF = this._raf.bind(this);
    this._resizeObserver = new ResizeObserver(()=> this._onResize());
    this._resizeObserver.observe(this);

    // initial size
    this._onResize();
    requestAnimationFrame(this._boundRAF);
  }

  connectedCallback(){
    // nothing to do
  }

  disconnectedCallback(){
    this._resizeObserver.disconnect();
    this._controls.dispose();
    this._renderer.dispose();
  }

  attributeChangedCallback(name, oldV, newV){
    if(newV === null) return;
    const n = Number(newV);
    const prop = name.replace(/-([a-z])/g, (_,c)=>c.toUpperCase());
    this._state[prop] = isNaN(n) ? newV : n;
    // update camera transform if rotated/zoom changed
    if(['rotX','rotY','rotZ','zoom'].includes(prop)) this._applyView();
    // rebuild mesh if geometry attrs changed
    if(['length','width','depth','color','accent'].includes(prop)) this._rebuildNeedleMesh();
  }

  // Public API
  setValue(v, {animate=true} = {}){
    this._state.value = clamp(Number(v), this._state.min, this._state.max);
    if(!animate){ this._display.value = this._state.value; this._updateNeedleImmediate(); }
  }

  setAnchor(x,y){
    this._state.cx = Number(x);
    this._state.cy = Number(y);
    // anchor changes only affect how you translate canvas-overlay relative to your gauge.
    // For this demo the needle pivot is in object space; expose pivot reposition if needed.
  }

  setRotation({rotX,rotY,rotZ,zoom} = {}){
    if(rotX !== undefined) this._state.rotX = Number(rotX);
    if(rotY !== undefined) this._state.rotY = Number(rotY);
    if(rotZ !== undefined) this._state.rotZ = Number(rotZ);
    if(zoom !== undefined) this._state.zoom = Number(zoom);
    this._applyView();
  }

  exportPNG(width=1200, height=800){
    // render at requested resolution into an offscreen canvas and return blob
    const scale = Math.max(1, Math.min(4, width / this.clientWidth));
    const oldPR = this._renderer.getPixelRatio();
    this._renderer.setPixelRatio(scale);
    this._renderer.setSize(Math.round(width), Math.round(height), false);
    this._renderer.render(this._scene, this._camera);
    return new Promise((resolve) => {
      this._renderer.domElement.toBlob((blob) => {
        // restore
        this._renderer.setPixelRatio(oldPR);
        this._onResize();
        resolve(blob);
      }, 'image/png');
    });
  }

  // build / rebuild mesh
  _buildNeedleMesh(){
    // clear group
    while(this._needleGroup.children.length) this._needleGroup.remove(this._needleGroup.children[0]);

    const s = this._state;
    const shaftLength = Number(s.length);
    const shaftWidth = Number(s.width);
    const shaftDepth = Math.max(1, Number(s.depth));

    // material
    const metal = new THREE.MeshStandardMaterial({
      color: new THREE.Color(s.color || '#ffdf6b'),
      metalness: 0.2,
      roughness: 0.45,
      emissive: new THREE.Color(0x000000)
    });

    const accentMat = new THREE.MeshStandardMaterial({
      color: new THREE.Color(s.accent || '#c57c00'),
      metalness: 0.4,
      roughness: 0.35
    });

    // tapered shaft: use CylinderGeometry with top radius near zero to taper to tip.
    // Cylinder aligned along Y by default; we want along X (so rotate)
    const topRadius = 0.0;
    const bottomRadius = Math.max(0.6, shaftWidth * 0.5);
    const radialSegments = 10;
    const shaftGeo = new THREE.CylinderGeometry(bottomRadius, topRadius, shaftLength, radialSegments, 1, true);
    // rotate cylinder so axis is X
    shaftGeo.rotateZ(Math.PI / 2);
    shaftGeo.translate(shaftLength / 2 * 0.16, 0, 0); // shift such that base near pivot; small offset

    const shaft = new THREE.Mesh(shaftGeo, metal);
    shaft.castShadow = true;
    shaft.receiveShadow = false;
    this._needleGroup.add(shaft);

    // spine: thin rod along center for highlight (tiny cylinder)
    const spineGeo = new THREE.CylinderGeometry(shaftWidth * 0.06, shaftWidth * 0.06, shaftLength * 0.92, 8);
    spineGeo.rotateZ(Math.PI/2);
    spineGeo.translate(shaftLength * 0.06, 0, (shaftWidth*0.03));
    const spineMat = new THREE.MeshStandardMaterial({ color: 0xffffff, emissive: 0x000000, metalness: 0.0, roughness: 0.2, opacity: 0.7, transparent: true });
    const spine = new THREE.Mesh(spineGeo, spineMat);
    this._needleGroup.add(spine);

    // counterweight (disc) at pivot
    const cwR = Math.max(shaftWidth * 0.9, 7);
    const cwGeo = new THREE.CylinderGeometry(cwR, cwR, Math.max(4, shaftWidth*0.6), 36);
    cwGeo.rotateX(Math.PI/2);
    const cw = new THREE.Mesh(cwGeo, accentMat);
    cw.position.set(0, 0, 0);
    this._needleGroup.add(cw);

    // pivot cap (small)
    const capGeo = new THREE.CylinderGeometry(cwR*0.3, cwR*0.3, 2, 24);
    capGeo.rotateX(Math.PI/2);
    const capMat = new THREE.MeshStandardMaterial({ color: 0xe6eef8, metalness: 0.1, roughness: 0.35 });
    const cap = new THREE.Mesh(capGeo, capMat);
    cap.position.set(0, 0, cwR*0.02);
    this._needleGroup.add(cap);

    // adjust pivot origin: place pivot at (0,0,0) and position shaft such that its base is at pivot
    // we already translated shaft a bit; ensure entire needle grouped at origin
    // set initial rotation to point at default value
    this._updateNeedleImmediate();
  }

  _rebuildNeedleMesh(){
    this._buildNeedleMesh();
  }

  _updateNeedleImmediate(){
    // compute angle and set needle rotation
    const s = this._state;
    const angleDeg = valueToAngleDeg(this._display.value, s.min, s.max);
    // Convert 2D gauge angle (0 deg to right, positive clockwise) to three.js rotation about Z
    // Our geometry is aligned +X to right; three.js positive rotation is CCW looking down +Z.
    const a = - (angleDeg * Math.PI / 180);
    // apply rotation to group
    this._needleGroup.rotation.set(0, 0, a);
    // small roll to simulate rotZ attribute (spin needle around X)
    const rz = (Number(s.rotZ)||0) * (Math.PI/180);
    this._needleGroup.rotation.z = a + rz;
  }

  _applyView(){
    const s = this._state;
    // rotate camera to provide the combined view rotation; easier: set camera position and lookAt
    const rx = (Number(s.rotX) || 0) * (Math.PI/180);
    const ry = (Number(s.rotY) || 0) * (Math.PI/180);
    const zoom = Number(s.zoom) || 1;
    // place camera on a sphere and point to origin
    const r = 360 / zoom;
    const cx = r * Math.sin(rx) * Math.cos(ry);
    const cy = r * Math.sin(ry);
    const cz = r * Math.cos(rx) * Math.cos(ry);
    this._camera.position.set(cx, cy, cz);
    this._camera.lookAt(0, 0, 0);
    this._camera.updateProjectionMatrix();
    // set light roughly relative to camera for pleasing specular
    this._dirLight.position.set(cx + 120, cy + 160, cz + 100);
  }

  _onResize(){
    const rect = this.getBoundingClientRect();
    const w = Math.max(16, Math.floor(rect.width));
    const h = Math.max(16, Math.floor(rect.height));
    this._renderer.setSize(w, h, false);
    this._camera.aspect = w / h;
    this._camera.updateProjectionMatrix();
  }

  _raf(){
    // smoothing
    const alpha = Number(this._state.smoothing) || 0.18;
    const tgt = this._state.value;
    this._display.value += (tgt - this._display.value) * alpha;
    // update
    this._updateNeedleImmediate();
    this._applyView();
    this._renderer.render(this._scene, this._camera);
    requestAnimationFrame(this._boundRAF);
  }
}

// helper clamp
function clamp(v, a, b){ return Math.max(a, Math.min(b, v)); }

customElements.define('silica-needle-three', ThreeNeedle);
