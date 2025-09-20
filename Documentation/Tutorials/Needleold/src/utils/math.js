export const toRad = d => d * Math.PI / 180;
export const toDeg = r => r * 180 / Math.PI;
export const clamp = (v,a,b) => Math.max(a, Math.min(b, v));
export function lerp(a,b,t){ return a + (b - a) * t; }

// Map value in [min,max] -> angleDegrees from startAngle to endAngle
export function valueToAngleDeg(v, min, max, startAngle = 135, endAngle = 45){
  if(max === min) return (startAngle + endAngle)/2;
  const t = clamp((v - min) / (max - min), 0, 1);
  let a0 = startAngle, a1 = endAngle;
  if(a1 <= a0) a1 += 360;
  return a0 + (a1 - a0) * t;
}
