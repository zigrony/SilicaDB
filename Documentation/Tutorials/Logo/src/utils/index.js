// Utilities exported for renderer and tests
export const toRad = d => d * Math.PI / 180;
export const clamp = (v,a,b) => Math.max(a, Math.min(b, v));
export const normalize = (v) => {
  const s = Math.hypot(...v) || 1;
  return [v[0]/s, v[1]/s, v[2]/s];
};
export const hex = (r,g,b)=>('#'+[r,g,b].map(x=>x.toString(16).padStart(2,'0')).join(''));

export function valueToAngleDeg(v, minVal, maxVal, startAngle = 135, endAngle = 45){
  if(maxVal === minVal) return (startAngle + ((endAngle - startAngle)/2));
  const t = clamp((v - minVal) / (maxVal - minVal), 0, 1);
  let a0 = startAngle, a1 = endAngle;
  if(a1 <= a0) a1 += 360;
  return a0 + (a1 - a0) * t;
}

export function buildRibbonPathPoints(a0, a1, r, curvatureZ, minSamples = 32){
  const samples = Math.max(minSamples, Math.round(Math.abs(a1 - a0) / (Math.PI*2) * 96));
  const pts = [];
  for(let i=0;i<=samples;i++){
    const t = i / samples;
    const a = a0 + (a1 - a0) * t;
    const x = Math.cos(a) * r;
    const y = Math.sin(a) * r;
    const nx = Math.cos(a), ny = Math.sin(a);
    const dA = (a1 - a0) / Math.max(1, samples);
    const tangential = Math.abs(r * dA);
    const nz = curvatureZ + 0.5 * Math.min(0.5, tangential / Math.max(1, r));
    pts.push({ x, y, a, nx, ny, nz });
  }
  return pts;
}

export function ptsToPathFromPairs(outerPts, innerPts){
  let d = '';
  for(let i=0;i<outerPts.length;i++){
    const [x,y] = outerPts[i];
    d += (i===0? 'M ':'L ') + x.toFixed(2) + ' ' + y.toFixed(2) + ' ';
  }
  for(let i=innerPts.length-1;i>=0;i--){
    const [x,y] = innerPts[i];
    d += 'L ' + x.toFixed(2) + ' ' + y.toFixed(2) + ' ';
  }
  d += 'Z';
  return d;
}

export function getRgbFromHex(h){
  const n = parseInt(h.slice(1),16);
  return [(n>>16)&255, (n>>8)&255, n&255];
}

export function rgbToHex(r,g,b){ return hex(r,g,b); }

// simple Fresnel+Blinn shading returning hex color
export function shadeHex(hexColor, normal, shininess = 36, baseAmbient = 0.06, F0 = 0.04, diffuseBoost = 0.92, specBoost = 0.9){
  const lightDir = normalize([0.45, -0.7, 0.55]);
  const viewDir = normalize([0,0,1]);
  const n = normalize(normal);
  const [r,g,b] = getRgbFromHex(hexColor);
  const Ld = Math.max(0, n[0]*lightDir[0] + n[1]*lightDir[1] + n[2]*lightDir[2]);
  const H = normalize([lightDir[0] + viewDir[0], lightDir[1] + viewDir[1], lightDir[2] + viewDir[2]]);
  const spec = Math.pow(Math.max(0, n[0]*H[0] + n[1]*H[1] + n[2]*H[2]), shininess);
  const VdotN = clamp(Math.max(0, n[0]*viewDir[0] + n[1]*viewDir[1] + n[2]*viewDir[2]), 0, 1);
  const fresnel = F0 + (1 - F0) * Math.pow(1 - VdotN, 5);
  const ambient = baseAmbient;
  const diffuseFactor = ambient + diffuseBoost * Ld;
  const specFactor = specBoost * spec * fresnel;
  const intensity = clamp(diffuseFactor + specFactor, 0, 2.6);
  const ca = v => Math.max(0, Math.min(255, Math.round(v * intensity)));
  return rgbToHex(ca(r), ca(g), ca(b));
}

export function averageHexColors(hexs){
  if(!hexs || hexs.length===0) return '#000000';
  let r=0,g=0,b=0;
  for(const h of hexs){
    const [rr,gg,bb] = getRgbFromHex(h);
    r+=rr; g+=gg; b+=bb;
  }
  const n = hexs.length;
  return rgbToHex(Math.round(r/n), Math.round(g/n), Math.round(b/n));
}
