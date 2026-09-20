/** The booth page: GET /board. One file, no framework; polls /board/data every 1.5 s. Brand tokens mirror UITheme in Unity. */
export const BOARD_HTML = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>GOOSED. Goose Board</title>
<style>
:root{--bg:#0b0a08;--glass:rgba(255,255,255,.05);--line:rgba(255,255,255,.12);--ink:#f7f2e6;--muted:rgba(247,242,230,.58);--cream:#fff6de;--yolk:#ffb826;--danger:#fa402e;--brown:#4d2e12}
*{box-sizing:border-box}
html,body{margin:0;background:var(--bg);color:var(--ink);font-family:"Avenir Next Condensed","Avenir Next","Helvetica Neue",Helvetica,Arial,sans-serif;-webkit-font-smoothing:antialiased}
body{min-height:100vh;background:radial-gradient(1200px 600px at 20% -10%,rgba(255,184,38,.10),transparent 60%),radial-gradient(900px 500px at 110% 110%,rgba(250,64,46,.08),transparent 60%),var(--bg)}
.wrap{max-width:1440px;margin:0 auto;padding:28px 32px 40px}
header{display:flex;align-items:flex-end;justify-content:space-between;gap:24px;padding:6px 4px 22px}
.brand{display:flex;align-items:baseline;gap:18px}
.word{font-size:56px;font-weight:800;letter-spacing:.01em;color:var(--cream);line-height:1}
.word span{color:var(--yolk)}
.sub{font-size:20px;font-weight:600;letter-spacing:.14em;color:var(--muted);text-transform:uppercase}
.live{display:flex;align-items:center;gap:12px;font-size:20px;font-weight:600;letter-spacing:.08em;color:var(--muted);text-transform:uppercase}
.dot{width:12px;height:12px;border-radius:50%;background:var(--yolk);box-shadow:0 0 0 0 rgba(255,184,38,.6);animation:pulse 1.6s infinite}
.dot.off{background:var(--danger);animation:none}
@keyframes pulse{0%{box-shadow:0 0 0 0 rgba(255,184,38,.55)}70%{box-shadow:0 0 0 12px rgba(255,184,38,0)}100%{box-shadow:0 0 0 0 rgba(255,184,38,0)}}
.grid{display:grid;grid-template-columns:1.3fr 1fr;gap:24px}
@media (max-width:1000px){.grid{grid-template-columns:1fr}.word{font-size:44px}}
.card{background:var(--glass);border:1px solid var(--line);border-radius:28px;padding:26px 28px;backdrop-filter:blur(10px)}
h2{margin:0 0 18px;font-size:18px;font-weight:700;letter-spacing:.16em;color:var(--yolk);text-transform:uppercase}
table{width:100%;border-collapse:collapse;font-size:24px}
th{font-size:14px;font-weight:700;letter-spacing:.14em;color:var(--muted);text-transform:uppercase;text-align:left;padding:0 10px 10px;border-bottom:1px solid var(--line)}
td{padding:14px 10px;border-bottom:1px solid rgba(255,255,255,.06);white-space:nowrap}
tr.in td{animation:slide .5s ease-out}
@keyframes slide{from{opacity:0;transform:translateY(-6px)}to{opacity:1;transform:none}}
td.rank{width:64px;color:var(--yolk);font-weight:800;font-size:28px}
td.name{font-weight:700;font-size:28px;color:var(--cream)}
td.time{font-variant-numeric:tabular-nums;font-weight:800;font-size:28px}
td.num{color:var(--muted);text-align:right}
th.num{text-align:right}
.chip{display:inline-block;padding:4px 12px;border-radius:999px;font-size:14px;font-weight:800;letter-spacing:.1em}
.chip.goosed{background:rgba(250,64,46,.18);color:#ff8a7a}
.chip.out{background:rgba(255,184,38,.18);color:var(--yolk)}
.empty{padding:36px 8px;color:var(--muted);font-size:22px}
.gname{font-size:58px;font-weight:800;color:var(--cream);line-height:1.05;margin:2px 0 4px}
.gtitle{font-size:20px;letter-spacing:.1em;color:var(--muted);text-transform:uppercase;margin-bottom:22px}
.stats{display:grid;grid-template-columns:repeat(4,1fr);gap:12px;margin-bottom:20px}
.stat{background:rgba(0,0,0,.28);border:1px solid var(--line);border-radius:18px;padding:14px 14px 12px}
.stat b{display:block;font-size:34px;font-weight:800;color:var(--cream);line-height:1}
.stat i{display:block;font-style:normal;font-size:12px;letter-spacing:.14em;color:var(--muted);text-transform:uppercase;margin-top:6px}
.shout{font-size:22px;color:var(--ink);margin:0 0 18px;min-height:30px}
.shout small{display:block;font-size:12px;letter-spacing:.14em;color:var(--muted);text-transform:uppercase;margin-bottom:4px}
.shout em{color:var(--yolk);font-style:italic}
ul.events{list-style:none;margin:0;padding:0;border-top:1px solid var(--line)}
ul.events li{display:grid;grid-template-columns:110px 1fr 64px;gap:12px;align-items:center;padding:11px 4px;border-bottom:1px solid rgba(255,255,255,.06);font-size:20px}
ul.events li.in{animation:slide .5s ease-out}
ul.events .kind{font-size:12px;font-weight:800;letter-spacing:.12em;text-transform:uppercase;color:var(--yolk);background:rgba(255,184,38,.12);border-radius:999px;padding:5px 10px;text-align:center}
ul.events .kind.caught{color:#ff8a7a;background:rgba(250,64,46,.16)}
ul.events .kind.outlasted{color:#9be29b;background:rgba(120,220,120,.14)}
ul.events .line{color:var(--ink);overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
ul.events .ago{color:var(--muted);font-size:14px;text-align:right}
footer{margin-top:26px;color:var(--muted);font-size:15px;letter-spacing:.04em;text-align:center}
</style>
</head>
<body>
<div class="wrap">
  <header>
    <div class="brand"><div class="word">GOOSED<span>.</span></div><div class="sub">Goose Board</div></div>
    <div class="live"><span id="total">&nbsp;</span><span class="dot" id="dot"></span></div>
  </header>
  <div class="grid">
    <section class="card">
      <h2>Top geese today</h2>
      <table id="table" hidden>
        <thead><tr><th>#</th><th>Player</th><th>Survived</th><th>Outcome</th><th>Goose</th><th class="num">Honks</th><th class="num">Dodges</th></tr></thead>
        <tbody id="rows"></tbody>
      </table>
      <div class="empty" id="empty">No one has faced the goose yet.</div>
    </section>
    <section class="card">
      <h2>The goose's brain</h2>
      <div class="gname" id="gname">Waiting for a goose</div>
      <div class="gtitle" id="gtitle">nobody is playing right now</div>
      <div class="stats">
        <div class="stat"><b id="grudge">0</b><i>Grudge</i></div>
        <div class="stat"><b id="rounds">0</b><i>Rounds</i></div>
        <div class="stat"><b id="best">0:00.0</b><i>Best</i></div>
        <div class="stat"><b id="last">0:00.0</b><i>Last</i></div>
      </div>
      <p class="shout"><small>Last shout</small><em id="shout">nothing yet</em></p>
      <ul class="events" id="events"></ul>
    </section>
  </div>
  <footer>The goose is a Cloudflare Agent: its memory lives in a Durable Object per phone. Runs older than 24 hours fall off the board. Hack the North 2026.</footer>
</div>
<script>
(function(){
  var device = new URLSearchParams(location.search).get('device');
  var dot = document.getElementById('dot');
  var seenRuns = {}, seenEvents = {}, firstPaint = true;
  function el(id){ return document.getElementById(id); }
  function esc(s){ return String(s == null ? '' : s).replace(/[&<>"']/g, function(c){ return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]; }); }
  function fmt(s){ s = Math.max(0, Number(s) || 0); var m = Math.floor(s / 60), sec = Math.floor(s % 60), t = Math.floor((s * 10) % 10); return m + ':' + (sec < 10 ? '0' : '') + sec + '.' + t; }
  function ago(at){ var d = Math.max(0, (Date.now() - Number(at)) / 1000); if (d < 5) return 'now'; if (d < 60) return Math.floor(d) + 's'; if (d < 3600) return Math.floor(d / 60) + 'm'; return Math.floor(d / 3600) + 'h'; }
  function render(d){
    var runs = d.runs || [];
    el('total').textContent = (d.total || 0) + (d.total === 1 ? ' run today' : ' runs today');
    el('table').hidden = runs.length === 0;
    el('empty').hidden = runs.length > 0;
    var html = '';
    for (var i = 0; i < runs.length; i++) {
      var r = runs[i], key = r.name + '|' + r.at;
      var fresh = !firstPaint && !seenRuns[key];
      seenRuns[key] = true;
      html += '<tr class="' + (fresh ? 'in' : '') + '"><td class="rank">' + (i + 1) + '</td><td class="name">' + esc(r.name) + '</td><td class="time">' + fmt(r.seconds) + '</td>'
        + '<td><span class="chip ' + (r.outcome === 'OUTLASTED' ? 'out' : 'goosed') + '">' + (r.outcome === 'OUTLASTED' ? 'OUTLASTED' : 'GOOSED') + '</span></td>'
        + '<td>' + esc(r.goose) + '</td><td class="num">' + (r.honks || 0) + '</td><td class="num">' + (r.dodges || 0) + '</td></tr>';
    }
    el('rows').innerHTML = html;
    var b = d.brain && d.brain.memory ? d.brain.memory : null;
    if (b && b.name) {
      el('gname').textContent = b.name;
      el('gtitle').textContent = b.title || 'a goose with a grudge';
      el('grudge').textContent = b.grudge || 0;
      el('rounds').textContent = b.rounds || 0;
      el('best').textContent = fmt(b.bestTime);
      el('last').textContent = fmt(b.lastTime);
      el('shout').textContent = b.lastShout ? '"' + b.lastShout + '"' : 'nothing yet';
      var ev = (d.brain.events || []).slice(0, 12), eh = '';
      for (var j = 0; j < ev.length; j++) {
        var e = ev[j], ek = e.kind + '|' + e.at;
        var efresh = !firstPaint && !seenEvents[ek];
        seenEvents[ek] = true;
        eh += '<li class="' + (efresh ? 'in' : '') + '"><span class="kind ' + esc(e.kind) + '">' + esc(e.kind).replace('_', ' ') + '</span><span class="line">' + esc(e.line || '') + '</span><span class="ago">' + ago(e.at) + '</span></li>';
      }
      el('events').innerHTML = eh;
    } else {
      el('gname').textContent = 'Waiting for a goose';
      el('gtitle').textContent = 'nobody is playing right now';
    }
    firstPaint = false;
  }
  async function tick(){
    try {
      var r = await fetch('/board/data' + (device ? '?device=' + encodeURIComponent(device) : ''), { cache: 'no-store' });
      if (!r.ok) throw new Error('http ' + r.status);
      render(await r.json());
      dot.className = 'dot';
    } catch (e) {
      dot.className = 'dot off';
    }
  }
  tick();
  setInterval(function(){ if (document.visibilityState !== 'hidden') tick(); }, 1500);
})();
</script>
</body>
</html>`;
