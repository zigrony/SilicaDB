# silica-needle-three

A needle Web Component rendered with three.js. The needle is a small 3D mesh (tapered shaft + counterweight) with lights and material, composited into a transparent canvas so you can overlay it on an SVG gauge.

Run demo:
1. npm install
2. npm start
3. Open http://localhost:5000/demo/index.html

Component API (silica-needle-three):
- Attributes: value, min, max, cx, cy, length, width, color, accent, smoothing, rot-x, rot-y, rot-z, zoom
- Methods:
  - setValue(n, {animate:true})
  - setAnchor(x,y)
  - setRotation({rotX,rotY,rotZ,zoom})
  - exportPNG(width,height): Promise<Blob>
- Events:
  - needle-input (detail: {value, angle})

The component sizes itself to the element size. For overlaying a gauge SVG, place this element over the gauge and set cx/cy to match the gauge coordinate system (demo aligns to a viewBox centered coordinate system).
