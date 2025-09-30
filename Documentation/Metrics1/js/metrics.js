async function fetchMetrics() {
  const res = await fetch('/api/metrics');
  return res.json();
}

function groupMetrics(metrics) {
  const emitted = metrics.filter(m => m.name === "diagcore.traces.emitted");
  const latencies = metrics.filter(m => m.name === "diagcore.traces.sink_emit_duration_ms");
  return { emitted, latencies };
}

function avg(values) {
  return values.length ? values.reduce((a, b) => a + b, 0) / values.length : 0;
}

document.addEventListener("DOMContentLoaded", async () => {
  const ctxEmittedChart = document.getElementById("emittedChart").getContext("2d");
  const ctxLatencyChart = document.getElementById("latencyChart").getContext("2d");

  // --- Gauges (Gauge.js) ---
  const gaugeOpts = {
    angle: 0,
    lineWidth: 0.2,
    radiusScale: 1,
    pointer: { length: 0.6, strokeWidth: 0.04, color: '#fff' },
    limitMax: false,
    limitMin: false,
    colorStart: '#22c55e',
    colorStop: '#ef4444',
    strokeColor: '#1e293b',
    generateGradient: true
  };

  const emittedGauge = new Gauge(document.getElementById("emittedGauge")).setOptions(gaugeOpts);
  emittedGauge.maxValue = 500;
  emittedGauge.setMinValue(0);
  emittedGauge.animationSpeed = 32;
  emittedGauge.set(0);

  const latencyGauge = new Gauge(document.getElementById("latencyGauge")).setOptions(gaugeOpts);
  latencyGauge.maxValue = 50;
  latencyGauge.setMinValue(0);
  latencyGauge.animationSpeed = 32;
  latencyGauge.set(0);

  // --- Line chart for emitted traces ---
  const emittedChart = new Chart(ctxEmittedChart, {
    type: 'line',
    data: { labels: [], datasets: [{ label: 'Traces Emitted', data: [], borderColor: '#38bdf8', fill: false }] },
    options: { scales: { x: { display: false } } }
  });

  // --- Histogram chart for latency ---
  const latencyChart = new Chart(ctxLatencyChart, {
    type: 'bar',
    data: { labels: [], datasets: [{ label: 'Latency (ms)', data: [], backgroundColor: '#f97316' }] },
    options: { scales: { y: { beginAtZero: true } } }
  });

  async function update() {
    const metrics = await fetchMetrics();
    const { emitted, latencies } = groupMetrics(metrics);

    // Update gauges
    emittedGauge.set(emitted.length);
    const avgLatency = avg(latencies.map(l => l.value));
    latencyGauge.set(avgLatency);

    // Update line chart
    emittedChart.data.labels.push(new Date().toLocaleTimeString());
    emittedChart.data.datasets[0].data.push(emitted.length);
    if (emittedChart.data.labels.length > 20) {
      emittedChart.data.labels.shift();
      emittedChart.data.datasets[0].data.shift();
    }
    emittedChart.update();

    // Update histogram
    latencyChart.data.labels = latencies.map(l => l.tags.find(t => t.key === "sink")?.value || "unknown");
    latencyChart.data.datasets[0].data = latencies.map(l => l.value);
    latencyChart.update();
  }

  setInterval(update, 3000); // refresh every 3s
});
