namespace QuantConnect.Research.Runner
{
    /// <summary>
    /// The live page, served as a single self-contained document.
    ///
    /// Kept as one file with no build step and no external assets on purpose: this is a research
    /// tool people open locally, and a page that needs a bundler, a CDN or a node install before it
    /// will render is a page that does not work when something is already broken. The chart is
    /// drawn on a canvas rather than pulled from a library, so the equity curve renders even if the
    /// machine is offline.
    /// </summary>
    public static class Ui
    {
        public const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>QuantLab Research</title>
<style>
  :root {
    --bg: #0e1116; --panel: #161b22; --line: #262d38; --text: #e6edf3;
    --dim: #8b949e; --green: #3fb950; --red: #f85149; --blue: #58a6ff; --amber: #d29922;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; background: var(--bg); color: var(--text);
    font: 14px/1.5 ui-sans-serif, system-ui, -apple-system, "Segoe UI", sans-serif;
  }
  header {
    padding: 14px 20px; border-bottom: 1px solid var(--line);
    display: flex; align-items: baseline; gap: 14px; flex-wrap: wrap;
  }
  header h1 { font-size: 16px; margin: 0; font-weight: 600; letter-spacing: .2px; }
  .status { color: var(--dim); font-size: 13px; }
  .status .dot {
    display: inline-block; width: 8px; height: 8px; border-radius: 50%;
    background: var(--dim); margin-right: 6px; vertical-align: middle;
  }
  .status.live .dot { background: var(--green); animation: pulse 1.4s infinite; }
  .status.failed .dot { background: var(--red); }
  @keyframes pulse { 0%,100% { opacity: 1 } 50% { opacity: .25 } }

  main { display: grid; grid-template-columns: 320px 1fr; gap: 16px; padding: 16px 20px; align-items: start; }
  @media (max-width: 900px) { main { grid-template-columns: 1fr; } }

  .panel { background: var(--panel); border: 1px solid var(--line); border-radius: 8px; padding: 14px; }
  .panel h2 {
    font-size: 11px; text-transform: uppercase; letter-spacing: .8px;
    color: var(--dim); margin: 0 0 12px; font-weight: 600;
  }
  label { display: block; font-size: 12px; color: var(--dim); margin-bottom: 10px; }
  label span { display: block; margin-bottom: 3px; }
  input, select, button {
    width: 100%; padding: 7px 9px; background: #0d1117; color: var(--text);
    border: 1px solid var(--line); border-radius: 5px; font: inherit; font-size: 13px;
  }
  input:focus, select:focus { outline: none; border-color: var(--blue); }
  button {
    cursor: pointer; font-weight: 600; background: #1f6feb; border-color: #1f6feb; margin-top: 4px;
  }
  button:hover:not(:disabled) { background: #388bfd; }
  button.stop { background: #21262d; border-color: var(--line); }
  button.stop:hover:not(:disabled) { background: #30363d; }
  button:disabled { opacity: .45; cursor: not-allowed; }

  .stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 10px; margin-bottom: 14px; }
  .stat { background: var(--panel); border: 1px solid var(--line); border-radius: 8px; padding: 10px 12px; }
  .stat .k { font-size: 11px; color: var(--dim); text-transform: uppercase; letter-spacing: .5px; }
  .stat .v { font-size: 19px; font-weight: 600; margin-top: 3px; font-variant-numeric: tabular-nums; }
  .pos { color: var(--green); } .neg { color: var(--red); }

  canvas { width: 100%; height: 300px; display: block; }
  table { width: 100%; border-collapse: collapse; font-size: 12.5px; font-variant-numeric: tabular-nums; }
  th, td { text-align: right; padding: 6px 8px; border-bottom: 1px solid var(--line); white-space: nowrap; }
  th { color: var(--dim); font-weight: 600; font-size: 11px; text-transform: uppercase; letter-spacing: .4px; }
  th:first-child, td:first-child { text-align: left; }
  tbody tr:last-child td { border-bottom: none; }
  .win { color: var(--green); } .loss { color: var(--red); }
  .empty { color: var(--dim); padding: 18px 0; text-align: center; font-size: 13px; }

  #log {
    max-height: 190px; overflow-y: auto; font-family: ui-monospace, "SF Mono", Menlo, monospace;
    font-size: 11.5px; color: var(--dim); background: #0d1117;
    border: 1px solid var(--line); border-radius: 6px; padding: 8px 10px;
  }
  #log div { padding: 1px 0; }
  #log .err { color: var(--red); }
  .note { color: var(--dim); font-size: 12px; margin-top: 10px; }
  code { background: #0d1117; padding: 1px 5px; border-radius: 3px; font-size: 12px; }
</style>
</head>
<body>
<header>
  <h1>QuantLab Research</h1>
  <span class="status" id="status"><span class="dot"></span><span id="statusText">idle</span></span>
  <span class="status" id="progress"></span>
</header>

<main>
  <div class="panel">
    <h2>Strategy</h2>
    <label><span>Exchange</span>
      <select id="provider">
        <option value="bybit" selected>Bybit</option>
        <option value="binance">Binance</option>
        <option value="okx">OKX</option>
      </select>
    </label>
    <label><span>Symbol</span><input id="symbol" value="BTCUSDT"></label>
    <label><span>Entry condition</span><input id="entry" value="trade_flow &lt; 0"></label>
    <label><span>Exit condition</span><input id="exit" value="trade_flow &gt;= 0"></label>
    <label><span>Direction</span>
      <select id="direction"><option value="long" selected>Long</option><option value="short">Short</option></select>
    </label>
    <label><span>Session length (s)</span><input id="duration" type="number" value="180" min="10" max="3600"></label>
    <label><span>Observation interval (s)</span><input id="interval" type="number" value="2" min="1"></label>
    <label><span>Hold (observations)</span><input id="holding" type="number" value="10" min="1"></label>
    <label><span>Position fraction</span><input id="fraction" value="0.5"></label>
    <label><span>Fee (bps)</span><input id="fees" value="5"></label>
    <button id="start">Start live session</button>
    <button class="stop" id="stop" disabled>Stop</button>
    <p class="note">
      A live session re-runs the engine over everything captured so far, so what you see is the same
      account a single run over the same data would produce. Equity and trades are published only
      when they change.
    </p>
  </div>

  <div>
    <div class="stats" id="stats"></div>

    <div class="panel" style="margin-bottom:16px">
      <h2>Equity</h2>
      <canvas id="chart"></canvas>
    </div>

    <div class="panel" style="margin-bottom:16px">
      <h2>Trades</h2>
      <div id="trades" class="empty">no trades yet</div>
    </div>

    <div class="panel">
      <h2>Log</h2>
      <div id="log"></div>
    </div>
  </div>
</main>

<script>
const $ = id => document.getElementById(id);
let runId = null, source = null;
const equity = [], trades = [];

function setStatus(text, cls) {
  $('statusText').textContent = text;
  $('status').className = 'status' + (cls ? ' ' + cls : '');
}
function log(message, isError) {
  const el = document.createElement('div');
  el.textContent = new Date().toLocaleTimeString() + '  ' + message;
  if (isError) el.className = 'err';
  const box = $('log');
  box.appendChild(el);
  while (box.children.length > 300) box.removeChild(box.firstChild);
  box.scrollTop = box.scrollHeight;
}
const num = (v, d = 2) => typeof v === 'number' ? v.toLocaleString(undefined, {
  minimumFractionDigits: d, maximumFractionDigits: d }) : '-';
const pct = v => typeof v === 'number' ? (v * 100).toFixed(2) + '%' : '-';
const signClass = v => typeof v === 'number' ? (v > 0 ? 'pos' : v < 0 ? 'neg' : '') : '';

function renderStats(s) {
  $('stats').innerHTML = [
    ['Equity', num(s.equity), ''],
    ['Return', pct(s.totalReturn), signClass(s.totalReturn)],
    ['Realized', num(s.realized), signClass(s.realized)],
    ['Unrealized', num(s.unrealized), signClass(s.unrealized)],
    ['Max drawdown', pct(s.maxDrawdown), 'neg'],
    ['Peak equity', num(s.peakEquity), ''],
    ['Trades', s.trades ?? '-', ''],
    ['Win rate', pct(s.winRate), ''],
    ['Fees', num(s.fees), '']
  ].map(([k, v, c]) => `<div class="stat"><div class="k">${k}</div><div class="v ${c}">${v}</div></div>`)
   .join('');
}

function renderTrades() {
  if (!trades.length) { $('trades').className = 'empty'; $('trades').textContent = 'no trades yet'; return; }
  $('trades').className = '';
  const rows = trades.slice().reverse().map(t => `
    <tr>
      <td>${String(t.side || '').toLowerCase()}</td>
      <td>${num(t.entryPrice)}</td>
      <td>${num(t.exitPrice)}</td>
      <td>${num(t.quantity, 4)}</td>
      <td class="${t.pnl > 0 ? 'win' : 'loss'}">${num(t.pnl)}</td>
      <td>${num(t.fees)}</td>
      <td>${new Date(t.exitTime).toLocaleTimeString()}</td>
    </tr>`).join('');
  $('trades').innerHTML = `
    <table><thead><tr><th>Side</th><th>Entry</th><th>Exit</th><th>Qty</th><th>P&amp;L</th>
    <th>Fees</th><th>Exited</th></tr></thead><tbody>${rows}</tbody></table>`;
}

function drawChart() {
  const canvas = $('chart'), ratio = window.devicePixelRatio || 1;
  const w = canvas.clientWidth, h = canvas.clientHeight;
  canvas.width = w * ratio; canvas.height = h * ratio;
  const ctx = canvas.getContext('2d');
  ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
  ctx.clearRect(0, 0, w, h);

  if (equity.length < 2) {
    ctx.fillStyle = '#8b949e'; ctx.font = '13px system-ui'; ctx.textAlign = 'center';
    ctx.fillText('waiting for equity data', w / 2, h / 2);
    return;
  }

  const values = equity.map(p => p.equity);
  const start = 100000;
  let lo = Math.min(...values, start), hi = Math.max(...values, start);
  if (hi - lo < 1e-9) { hi += 1; lo -= 1; }
  const pad = 22, x = i => pad + (w - 2 * pad) * (i / (equity.length - 1));
  const y = v => h - pad - (h - 2 * pad) * ((v - lo) / (hi - lo));

  // starting-cash baseline: the line that separates making money from not
  ctx.strokeStyle = '#30363d'; ctx.setLineDash([4, 4]);
  ctx.beginPath(); ctx.moveTo(pad, y(start)); ctx.lineTo(w - pad, y(start)); ctx.stroke();
  ctx.setLineDash([]);

  const up = values[values.length - 1] >= start;
  ctx.strokeStyle = up ? '#3fb950' : '#f85149'; ctx.lineWidth = 2;
  ctx.beginPath();
  values.forEach((v, i) => i ? ctx.lineTo(x(i), y(v)) : ctx.moveTo(x(i), y(v)));
  ctx.stroke();

  // area under the line, for legibility
  ctx.lineTo(x(values.length - 1), h - pad); ctx.lineTo(x(0), h - pad); ctx.closePath();
  ctx.fillStyle = up ? 'rgba(63,185,80,.10)' : 'rgba(248,81,73,.10)'; ctx.fill();

  ctx.fillStyle = '#8b949e'; ctx.font = '11px system-ui'; ctx.textAlign = 'left';
  ctx.fillText(num(hi), pad, 12);
  ctx.fillText(num(lo), pad, h - 4);
  ctx.textAlign = 'right';
  ctx.fillText(`${equity.length} marks`, w - pad, h - 4);
}

function connect(id) {
  runId = id;
  source = new EventSource(`/api/live/${id}/stream`);
  source.onmessage = e => {
    const msg = JSON.parse(e.data);
    switch (msg.type) {
      case 'status':
        setStatus(msg.status, 'live'); log(msg.message || msg.status); break;
      case 'equity':
        equity.push(msg);
        $('progress').textContent =
          `${msg.openPositions || 0} open position${msg.openPositions === 1 ? '' : 's'}`;
        drawChart(); break;
      case 'summary':
        renderStats(msg); break;
      case 'trade':
        trades.push(msg); renderTrades();
        log(`${msg.side} ${msg.quantity} @ ${msg.entryPrice} -> ${msg.exitPrice}  ` +
            `P&L ${num(msg.pnl)}`, msg.pnl < 0); break;
      case 'progress':
        $('progress').textContent =
          `${msg.observations} observations, ${num(msg.events, 0)} events`; break;
      case 'log':
        log(msg.message); break;
      case 'error':
        log(msg.message + (msg.detail ? ' — ' + String(msg.detail).split('\n').pop() : ''), true);
        break;
      case 'complete':
        setStatus(msg.status, msg.status === 'completed' ? '' : 'failed');
        log(msg.message);
        source.close(); $('stop').disabled = true; $('start').disabled = false;
        drawChart(); break;
    }
  };
  source.onerror = () => {
    // EventSource reconnects on its own; only surface it if the session is genuinely gone.
    fetch(`/api/live/${id}`).then(r => r.ok ? null : (setStatus('session lost', 'failed'),
      log('session is no longer available', true)));
  };
}

$('start').onclick = async () => {
  $('start').disabled = true; $('stop').disabled = false;
  equity.length = 0; trades.length = 0; renderTrades(); drawChart();
  $('stats').innerHTML = ''; $('log').innerHTML = '';
  setStatus('starting', 'live');

  const body = {
    provider: $('provider').value, symbol: $('symbol').value,
    entryCondition: $('entry').value, exitCondition: $('exit').value,
    direction: $('direction').value,
    durationSeconds: +$('duration').value, observationIntervalSeconds: +$('interval').value,
    holdingObservations: +$('holding').value, positionFraction: $('fraction').value,
    feeBps: $('fees').value
  };

  try {
    const res = await fetch('/api/live', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
    });
    const data = await res.json();
    if (!res.ok) throw new Error(data.error || 'could not start');
    log(`session ${data.runId}`);
    connect(data.runId);
  } catch (e) {
    setStatus('failed', 'failed'); log(e.message, true);
    $('start').disabled = false; $('stop').disabled = true;
  }
};

$('stop').onclick = async () => {
  if (runId) { await fetch(`/api/live/${runId}/stop`, { method: 'POST' }); log('stop requested'); }
  if (source) source.close();
  $('stop').disabled = true; $('start').disabled = false;
};

window.addEventListener('resize', drawChart);
drawChart();
</script>
</body>
</html>
""";
    }
}
