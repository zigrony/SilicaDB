let chart;
let chartData = {};
let refreshTimer;

function escapeHtml(text) {
  if (!text) return '';
  return text.replace(/[&<>\"']/g, m => ({
    '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'
  }[m]));
}

function renderTags(tags) {
  if (!tags) return '';
  if (Array.isArray(tags)) {
    return tags.map(t => {
      const k = t.Key || t.key;
      const v = t.Value || t.value;
      return `${escapeHtml(k)}=${escapeHtml(String(v))}`;
    }).join(', ');
  }
  if (typeof tags === 'object') {
    return Object.entries(tags)
      .map(([k, v]) => `${escapeHtml(k)}=${escapeHtml(String(v))}`)
      .join(', ');
  }
  return '';
}

function gaugeClass(value, type) {
  if (type === 'histogram') {
    if (value < 200) return 'green';
    if (value < 500) return 'yellow';
    return 'red';
  }
  if (type === 'counter') {
    if (value < 100) return 'green';
    if (value < 500) return 'yellow';
    return 'red';
  }
  return '';
}

function renderGauge(value, type) {
  if (typeof value !== 'number' || isNaN(value)) return '';
  const max = type === 'counter' ? 1000 : 1000;
  const pct = Math.min(100, (value/max)*100);
  const cls = gaugeClass(value, type);
  return `<div class="gauge ${cls}"><div class="gauge-fill" style="width:${pct}%"></div></div>
          <span class="gauge-label">${value.toFixed(2)}</span>`;
}

let rowMap = {}; // key -> <tr> element

async function loadMetrics() {
  const count = document.getElementById('metricCount');
  try {
    const res = await fetch('/api/metrics');
    const metrics = await res.json();

    const latest = {};
    for (const m of metrics) {
      const component = (m.tags?.find(t => t.key === 'component' || t.Key === 'component')?.value) || '';
      const key = m.name + '|' + component;
      latest[key] = m;

      if (!rowMap[key]) {
        // create new row
        const tr = document.createElement('tr');
        tr.innerHTML = `
          <td class="name"></td>
          <td class="component"></td>
          <td class="type"></td>
          <td class="value"></td>
          <td class="gauge-cell"></td>
          <td class="tags"></td>
          <td class="chart-cell"><button>View</button></td>`;
        tr.querySelector('button').onclick = () => showChart(m.name, component);
        document.getElementById('metricsBody').appendChild(tr);
        rowMap[key] = tr;
      }

      // update row
      const tr = rowMap[key];
      tr.querySelector('.name').textContent = m.name;
      tr.querySelector('.component').textContent = component;
      tr.querySelector('.type').textContent = m.type;
      tr.querySelector('.value').textContent = m.value.toFixed ? m.value.toFixed(2) : m.value;
      tr.querySelector('.gauge-cell').innerHTML = renderGauge(Number(m.value), m.type);
      tr.querySelector('.tags').innerHTML = renderTags(m.tags);
    }

    // remove rows that disappeared
    for (const key in rowMap) {
      if (!latest[key]) {
        rowMap[key].remove();
        delete rowMap[key];
      }
    }

    count.textContent = Object.keys(latest).length + ' metrics';
  } catch (e) {
    console.error("Error loading metrics", e);
  }
}

function clearMetrics() {
  document.getElementById('metricsBody').innerHTML = '';
  document.getElementById('metricCount').textContent = '';
}

function showChart(name, component) {
  const chartKey = name + '|' + component;
  const data = chartData[chartKey] || [];
  const ctx = document.getElementById('metricChart').getContext('2d');
  if (chart) chart.destroy();
  chart = new Chart(ctx, {
    type: 'line',
    data: {
      labels: data.map(d => d.t.toLocaleTimeString()),
      datasets: [{
        label: `${name} (${component})`,
        data: data.map(d => d.v),
        borderColor: '#4caf50',
        fill: false
      }]
    },
    options: {
      responsive: true,
      scales: {
        y: { beginAtZero: true }
      }
    }
  });
  document.getElementById('chartTitle').textContent = `${name} (${component})`;
  document.getElementById('chartPanel').classList.remove('hidden');
}

function closeChart() {
  document.getElementById('chartPanel').classList.add('hidden');
}

function flashCell(cell) {
  if (!cell) return;
  cell.classList.remove('metric-flash'); // reset if already animating
  void cell.offsetWidth; // force reflow to restart animation
  cell.classList.add('metric-flash');
}
let prevValues = {};

function updateRow(key, m, tr) {
  const valueCell = tr.querySelector('.value');
  const newVal = Number(m.value);

  // update text
  valueCell.textContent = isNaN(newVal) ? '' : newVal.toFixed(2);

  // flash if changed
  if (prevValues[key] !== newVal) {
    flashCell(valueCell);
    prevValues[key] = newVal;
  }

  // update gauge and flash it too
  tr.querySelector('.gauge-cell').innerHTML = renderGauge(newVal, m.type);
  flashCell(tr.querySelector('.gauge-cell'));
}

window.onload = () => {
  loadMetrics();
  refreshTimer = setInterval(() => {
    if (document.getElementById('autoRefresh').checked) {
      loadMetrics();
    }
  }, 15000);
};
