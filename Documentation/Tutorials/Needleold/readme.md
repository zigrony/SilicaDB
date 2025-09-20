# silica-needle

A focused Web Component that renders a 3D-looking SVG needle. The component is framework-agnostic,
small, and exposes a concise public API.

Features
- Declarative attributes: value, min, max, cx, cy, length, width, depth, steps, rotation-offset
- Methods: setValue(n, {animate}), setAnchor(x,y), exportSVG()
- Emits: needle-input (detail: {value, angle})
- Lightweight shading and layered extrusion to create a 3D appearance

Run demo:
1. npm install
2. npm start
3. open http://localhost:5000/demo/index.html
