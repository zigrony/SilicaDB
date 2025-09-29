const Logs = (() => {
  let allLogs = [];
  let sortField = 'timestamp';
  let sortAsc = false;
  let refreshTimer;

  function escapeHtml(text) {
    if (!text) return '';
    return text.replace(/[&<>\"']/g, m => ({
      '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'
    }[m]));
  }

  function statusClass(status) {
    switch ((status || '').toLowerCase()) {
      case 'info': return 'status-info';
      case 'warn': return 'status-warn';
      case 'error': return 'status-error';
      case 'fatal': return 'status-fatal';
      case 'debug': return 'status-debug';
      case 'trace': return 'status-trace';
      case 'ok': return 'status-ok';
      case 'start': return 'status-start';
      default: return '';
    }
  }

  function renderRow(l) {
    const ts = escapeHtml(l.timestamp || '');
    const status = escapeHtml(l.status || '');
    const component = escapeHtml(l.component || '');
    const operation = escapeHtml(l.operation || '');
    const message = escapeHtml(l.message || '');
    const correlationId = escapeHtml(l.correlationId || '');
    const spanId = escapeHtml(l.spanId || '');

    let tags = '';
    if (l.tags && typeof l.tags === 'object') {
      tags = Object.entries(l.tags)
        .map(([k,v]) => `${escapeHtml(k)}=${escapeHtml(v)}`)
        .join(', ');
    }

    let ex = '';
    if (l.exception && typeof l.exception === 'object') {
      const exType = escapeHtml(l.exception['ClassName'] || l.exception['type'] || 'Exception');
      const exMsg = escapeHtml(l.exception['Message'] || l.exception['message'] || '');
      ex = `ex=${exType}${exMsg ? ': ' + exMsg : ''}`;
    }

    return `
      <tr>
        <td>${ts}</td>
        <td class="${statusClass(status)}">${status}</td>
        <td>${component}</td>
        <td>${operation}</td>
        <td>${message}</td>
        <td class="details">
          ${tags ? '['+tags+'] ' : ''}${ex}
          <div class="log-meta">cid=${correlationId} sid=${spanId}</div>
        </td>
      </tr>`;
  }

  function applyFilters() {
    const search = document.getElementById('searchBox').value.toLowerCase();
    const statusFilter = document.getElementById('statusFilter').value.toLowerCase();
    const componentFilter = document.getElementById('componentFilter').value.toLowerCase();

    let filtered = allLogs.filter(l => {
      const s = (l.status || '').toLowerCase();
      const c = (l.component || '').toLowerCase();
      const text = JSON.stringify(l).toLowerCase();
      return (!statusFilter || s === statusFilter)
          && (!componentFilter || c === componentFilter)
          && (!search || text.includes(search));
    });

    // sort
    filtered.sort((a,b) => {
      let va = a[sortField] || '';
      let vb = b[sortField] || '';
      if (sortField === 'timestamp') {
        va = new Date(va).getTime();
        vb = new Date(vb).getTime();
      }
      if (va < vb) return sortAsc ? -1 : 1;
      if (va > vb) return sortAsc ? 1 : -1;
      return 0;
    });

    // render
    const body = document.getElementById('logsBody');
    body.innerHTML = filtered.map(renderRow).join('');
    document.getElementById('logCount').textContent = `${filtered.length} log${filtered.length===1?'':'s'}`;

    // update component filter options
    const compSel = document.getElementById('componentFilter');
    const comps = [...new Set(allLogs.map(l => l.component).filter(Boolean))].sort();
    compSel.innerHTML = '<option value="">All Components</option>' + comps.map(c => `<option value="${c}">${c}</option>`).join('');
    compSel.value = componentFilter;
  }

  async function load() {
    const body = document.getElementById('logsBody');
    const count = document.getElementById('logCount');
    body.innerHTML = `<tr><td colspan="6">Loading...</td></tr>`;
    try {
      const res = await fetch('/api/logs');
      if (!res.ok) {
        body.innerHTML = `<tr><td colspan="6">Failed to load logs: ${res.status}</td></tr>`;
        count.textContent = '';
        return;
      }
      const logs = await res.json();
      if (!logs || logs.length === 0) {
        body.innerHTML = `<tr><td colspan="6">No logs available.</td></tr>`;
        count.textContent = '';
        return;
      }
      allLogs = logs;
      applyFilters();
    } catch (e) {
      body.innerHTML = `<tr><td colspan="6">Error loading logs: ${escapeHtml(e.message || e)}</td></tr>`;
      count.textContent = '';
    }
  }

  function clear() {
    allLogs = [];
    document.getElementById('logsBody').innerHTML = '';
    document.getElementById('logCount').textContent = '';
  }

  function initSorting() {
    const headers = document.querySelectorAll('#logsTable th[data-sort]');
    headers.forEach(h => {
      h.addEventListener('click', () => {
        const field = h.getAttribute('data-sort');
        if (sortField === field) {
          sortAsc = !sortAsc;
        } else {
          sortField = field;
          sortAsc = true;
        }
        applyFilters();
      });
    });
  }

  function initAutoRefresh() {
    refreshTimer = setInterval(() => {
      if (document.getElementById('autoRefresh').checked) {
        load();
      }
    }, 5000);
  }

  // expose public API
  return {
    load,
    clear,
    applyFilters
  };
})();

// initialize on page load
window.onload = () => {
  Logs.load();
  Logs.applyFilters();
  // set up sorting and auto-refresh
  (function() {
    const headers = document.querySelectorAll('#logsTable th[data-sort]');
    headers.forEach(h => {
      h.addEventListener('click', () => {
        const field = h.getAttribute('data-sort');
        if (field) {
          if (field === sortField) {
            sortAsc = !sortAsc;
          } else {
            sortField = field;
            sortAsc = true;
          }
          Logs.applyFilters();
        }
      });
    });
  })();
  setInterval(() => {
    if (document.getElementById('autoRefresh').checked) {
      Logs.load();
    }
  }, 5000);
};
   