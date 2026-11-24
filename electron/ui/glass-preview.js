'use strict';

const previewShapes = new WeakMap();
const previewObserver = new ResizeObserver(entries => {
  for (const { target } of entries) updateGlassPreview(target);
});

function stageSampler(stage) {
  const style = getComputedStyle(stage);
  const color = token => style.getPropertyValue(token).trim().slice(1).match(/.{2}/g).map(hex => parseInt(hex, 16));
  const base = color('--stage-base');
  const stripe = color('--stage-stripe');
  const rail = color('--stage-rail');
  const grid = color('--stage-grid');
  const angle = 139 * Math.PI / 180;
  const nx = Math.sin(angle);
  const ny = -Math.cos(angle);
  const width = stage.clientWidth;
  const height = stage.clientHeight;
  const span = nx * width + ny * height;

  return (x, y) => {
    const line = .5 + (nx * (x - width / 2) + ny * (y - height / 2)) / span;
    const background = line >= .52 && line < .535 ? rail : line >= .33 && line < .34 ? stripe : base;
    if (x % 24 < 1 || (height - y) % 24 < 1) {
      return background.map((value, channel) => value + (grid[channel] - value) * grid[3] / 255);
    }
    return background;
  };
}

// Render a bent copy of the in-app stage at the rim. The real widget receives
// an equivalent edge layer from Win32, with no desktop image in this preview.
function buildPreviewRim(element, stage) {
  const width = element.offsetWidth;
  const height = element.offsetHeight;
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  const context = canvas.getContext('2d');
  const image = context.createImageData(width, height);
  const scene = stageSampler(stage);
  const initial = element.getBoundingClientRect();
  const backdrop = stage.getBoundingClientRect();
  const originX = initial.left - backdrop.left;
  const originY = initial.top - backdrop.top;
  const radius = 26;

  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const dx = x + .5 - width / 2;
      const dy = y + .5 - height / 2;
      const qx = Math.abs(dx) - width / 2 + radius;
      const qy = Math.abs(dy) - height / 2 + radius;
      const ax = Math.max(0, qx);
      const ay = Math.max(0, qy);
      const length = Math.hypot(ax, ay);
      const depth = radius - length - Math.min(0, Math.max(qx, qy));
      if (depth < 0 || depth >= 13) continue;

      const edge = 1 - depth / 13;
      const bend = 18 * (1 - Math.sqrt(1 - edge * edge));
      const nx = Math.sign(dx) * (length ? ax / length : Number(qx > qy));
      const ny = Math.sign(dy) * (length ? ay / length : Number(qy >= qx));
      const rgb = scene(originX + x - nx * bend, originY + y - ny * bend);
      const gleam = Math.pow(Math.abs(-.35 * nx - .7 * ny), 3) * edge * edge;
      const i = (y * width + x) * 4;
      for (let channel = 0; channel < 3; channel++) {
        image.data[i + channel] = rgb[channel] + (255 - rgb[channel]) * gleam;
      }
      image.data[i + 3] = 214 * Math.sqrt(edge);
    }
  }
  context.putImageData(image, 0, 0);
  return canvas.toDataURL('image/png');
}

function updateGlassPreview(element) {
  const available = element.dataset.theme === 'glass' && element.offsetWidth > 0 &&
    !document.documentElement.classList.contains('reduce-glass') &&
    !matchMedia('(prefers-reduced-transparency: reduce), (forced-colors: active)').matches;
  const rim = element.querySelector('.glass-rim');
  element.classList.toggle('preview-refraction', available);
  element.classList.toggle('sampled-glass', available);
  rim.hidden = !available;
  if (!available) return;

  const stage = element.closest('.preview-stage');
  const geometry = [element.offsetWidth, element.offsetHeight, stage.clientWidth, stage.clientHeight].join(':');
  if (!previewShapes.has(element)) previewObserver.observe(element);
  if (previewShapes.get(element) === geometry) return;
  rim.src = buildPreviewRim(element, stage);
  previewShapes.set(element, geometry);
}
