// Part 2 - Real-Time Command Stream & History.
//
// Data comes from three places:
//   /api/dashboard    polled every 1.5s (queues, alert sets, command stacks, engine output)
//   /api/devices      search / filter (server-side, uses the category index)
//   /api/events (SSE) live packet feed + alert toasts, opened once by the App shell
//
// Everything that should survive switching pages (filters, selected device,
// time range) is kept in sessionStorage-backed state, and the view itself
// stays mounted while hidden.

const STATE_FILTERS = ["All", "Critical", "Offline", "Warning", "Ok"];
const CATEGORY_FILTERS = ["All", "Environmental", "PowerConsumption", "Actuator"];

function CommandStreamView({ active, onOpenIngestion }) {
  const toast = useToast();
  const live = useContext(LiveContext);

  const [dash, reloadDash] = usePoll("/api/dashboard", 1500, active);

  const [query, setQuery] = useSessionState("cs.query", "");
  const [category, setCategory] = useSessionState("cs.category", "All");
  const [stateFilter, setStateFilter] = useSessionState("cs.state", "All");
  const [selectedMac, setSelectedMac] = useSessionState("cs.selected", "AA:BB:CC:00:02");
  const [devices, setDevices] = useState([]);
  const [busy, setBusy] = useState(false);

  // ---- device search / filter --------------------------------------------
  const buildDeviceQuery = useCallback((track) => {
    const p = new URLSearchParams();
    if (query.trim()) p.set("q", query.trim());
    if (category !== "All") p.set("category", category);
    if (stateFilter !== "All") p.set("state", stateFilter);
    if (track) p.set("track", "true");
    return `/api/devices?${p.toString()}`;
  }, [query, category, stateFilter]);

  const loadDevices = useCallback(async (track = false) => {
    const r = await api(buildDeviceQuery(track));
    if (r.ok) setDevices(r.data);
  }, [buildDeviceQuery]);

  // A change in the filters is a real user search -> tracked (debounced so
  // typing "pump" doesn't log p, pu, pum, pump).
  const firstRun = useRef(true);
  useEffect(() => {
    if (!active) return undefined;
    if (firstRun.current) { firstRun.current = false; loadDevices(false); return undefined; }
    const id = setTimeout(() => loadDevices(true), 450);
    return () => clearTimeout(id);
  }, [query, category, stateFilter]); // eslint-disable-line react-hooks/exhaustive-deps

  useInterval(() => loadDevices(false), 2000, active);
  useEffect(() => { if (active) loadDevices(false); }, [active]); // eslint-disable-line react-hooks/exhaustive-deps

  // ---- live alert toasts ---------------------------------------------------
  useEffect(() => live.subscribe((type, p) => {
    if (type === "alert" && p.kind === "raised" && p.level === "Critical") toast(`CRITICAL - ${p.deviceName}: ${p.message}`, "crit");
    else if (type === "alert" && p.kind === "offline") toast(`${p.deviceName} went offline`, "warn");
    else if (type === "automation") toast(`Auto-Engine: ${p.title}`, "auto");
  }), [live, toast]);

  // ---- actions -------------------------------------------------------------
  async function sendCommand(command, label) {
    setBusy(true);
    const r = await api("/api/commands", { method: "POST", body: JSON.stringify(command) });
    setBusy(false);
    toast(r.ok ? r.data.message : (r.data && r.data.message) || "Command failed", r.ok ? "good" : "crit");
    reloadDash();
    loadDevices(false);
  }

  async function undoRedo(which) {
    const r = await api(`/api/commands/${which}`, { method: "POST" });
    if (r.data) toast(r.data.message, r.data.success ? "info" : "warn");
    reloadDash();
    loadDevices(false);
  }

  async function setAutoMode(enabled) {
    await api(`/api/engine/automode?enabled=${enabled}`, { method: "POST" });
    toast(enabled ? "Auto mode ON - high-confidence actions will run automatically" : "Auto mode OFF", "info");
    reloadDash();
  }

  function focusDevice(mac) {
    setSelectedMac(mac);
    document.getElementById("timeline-card")?.scrollIntoView({ behavior: "smooth", block: "center" });
  }

  function runSuggestion(s) {
    if (s.actionKind === "command" && s.command) sendCommand({ ...s.command, issuedBy: "Operator" });
    else if (s.actionKind === "focus" && s.focusMac) { setQuery(""); focusDevice(s.focusMac); }
    else if (s.focusMac) setQuery(s.focusMac);
  }

  // Devices the engine flagged before anyone searched for them.
  const flagged = useMemo(() => {
    const m = new Map();
    (dash?.insights || []).forEach((i) => { if (i.deviceMac && !m.has(i.deviceMac)) m.set(i.deviceMac, i); });
    return m;
  }, [dash]);

  const stats = dash?.stats;
  const alerts = dash?.alerts;
  const engine = dash?.engine;

  return (
    <div className="cs">
      <div className="page-head">
        <div>
          <p className="eyebrow">Tab 2 &middot; Part 2</p>
          <h2>Real-Time Command Stream &amp; History</h2>
        </div>
        <label className={`auto-toggle ${engine?.autoMode ? "on" : ""}`}>
          <input type="checkbox" checked={!!engine?.autoMode} onChange={(e) => setAutoMode(e.target.checked)} />
          <span className="switch" aria-hidden="true" />
          <span>
            <strong>Auto mode</strong>
            <small>Run suggestions with &ge;70% confidence</small>
          </span>
        </label>
      </div>

      <KpiStrip stats={stats} alerts={alerts} live={live} />

      <div className="cs-main">
        <section className="card devices-card">
          <div className="card-head">
            <h3>Devices <span className="count">{devices.length}</span></h3>
            <span className="muted">Search runs on DeviceRegistry + CategoryIndex</span>
          </div>
          <FilterBar
            query={query} setQuery={setQuery}
            category={category} setCategory={setCategory}
            stateFilter={stateFilter} setStateFilter={setStateFilter}
          />
          <div className="device-grid">
            {devices.map((d) => (
              <DeviceTile
                key={d.mac}
                device={d}
                selected={d.mac === selectedMac}
                flagged={flagged.get(d.mac)}
                onSelect={() => setSelectedMac(d.mac)}
                onCommand={sendCommand}
                busy={busy}
              />
            ))}
            {devices.length === 0 && (
              <div className="empty">No devices match these filters. Clear the search or pick another state.</div>
            )}
          </div>
        </section>

        <aside className="cs-side">
          <SuggestionsPanel suggestions={dash?.suggestions} engine={engine} onRun={runSuggestion} />
          <InsightsPanel insights={dash?.insights} onView={focusDevice} />
        </aside>
      </div>

      <div className="cs-row">
        <TimelineCard mac={selectedMac} active={active} />
        <CommandStackCard commands={dash?.commands} onUndo={() => undoRedo("undo")} onRedo={() => undoRedo("redo")} onCommand={sendCommand} />
      </div>

      <div className="cs-row">
        <PipelineCard dash={dash} live={live} />
        <div className="cs-col">
          <SimulationCard onDone={reloadDash} active={active} />
          <AlertSetCard alerts={alerts} />
        </div>
      </div>

      <div className="cs-row">
        <PatternsCard active={active} engine={engine} />
        <StructuresCard dash={dash} />
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------

function KpiStrip({ stats, alerts, live }) {
  const s = stats || {};
  const std = s.standard || {};
  const pri = s.priority || {};
  const tiles = [
    { label: "Throughput", value: s.throughputPerSecond ?? "-", unit: "pkt/s", sub: `cap ${s.processingRatePerSecond ?? "-"}/s`, tone: "" },
    { label: "Standard queue", value: std.depth ?? "-", unit: "waiting", sub: `avg wait ${fmtMs(std.avgLatencyMs)}`, tone: std.depth > 50 ? "warn" : "" },
    { label: "Priority queue", value: pri.depth ?? "-", unit: "waiting", sub: `avg wait ${fmtMs(pri.avgLatencyMs)} · ${s.priorityBypasses ?? 0} bypasses`, tone: "prio" },
    { label: "Active errors", value: alerts?.activeErrors?.length ?? "-", unit: "in set", sub: `${alerts?.alertsRaised ?? 0} raised total`, tone: alerts?.activeErrors?.length ? "crit" : "good" },
    { label: "Offline nodes", value: alerts?.disconnected?.length ?? "-", unit: "in set", sub: "heartbeat > 12s", tone: alerts?.disconnected?.length ? "warn" : "good" },
    { label: "Duplicates blocked", value: alerts?.duplicatesSuppressed ?? "-", unit: "", sub: "by HashSet.Add()", tone: "" },
  ];
  return (
    <div className="kpis">
      {tiles.map((t) => (
        <div key={t.label} className={`kpi kpi-${t.tone}`}>
          <span className="kpi-label">{t.label}</span>
          <span className="kpi-value">{t.value}<small>{t.unit}</small></span>
          <span className="kpi-sub">{t.sub}</span>
        </div>
      ))}
      <div className={`kpi kpi-live ${live.connected ? "" : "kpi-warn"}`}>
        <span className="kpi-label">Live stream</span>
        <span className="kpi-value">{live.connected ? "SSE" : "..."}<small>{live.connected ? "connected" : "reconnecting"}</small></span>
        <span className="kpi-sub">{live.eventCount} events received</span>
      </div>
    </div>
  );
}

function fmtMs(ms) {
  if (ms == null) return "-";
  return ms >= 1000 ? `${(ms / 1000).toFixed(1)}s` : `${Math.round(ms)}ms`;
}

function FilterBar({ query, setQuery, category, setCategory, stateFilter, setStateFilter }) {
  return (
    <div className="filters">
      <div className="search">
        <svg viewBox="0 0 20 20" width="16" height="16" aria-hidden="true"><circle cx="8.5" cy="8.5" r="5.5" fill="none" stroke="currentColor" strokeWidth="2" /><path d="M13 13l4 4" stroke="currentColor" strokeWidth="2" strokeLinecap="round" /></svg>
        <input
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Search name, zone or MAC (e.g. pump, Bay 2, 00:05)"
          aria-label="Search devices"
        />
        {query && <button className="clear" onClick={() => setQuery("")} aria-label="Clear search">&times;</button>}
      </div>
      <div className="chip-row" role="group" aria-label="Category">
        {CATEGORY_FILTERS.map((c) => (
          <button key={c} className={`chip ${category === c ? "chip-on" : ""}`} onClick={() => setCategory(c)}>
            {c === "All" ? "All categories" : CATEGORY_LABEL[c]}
          </button>
        ))}
      </div>
      <div className="chip-row" role="group" aria-label="Alert state">
        {STATE_FILTERS.map((s) => (
          <button key={s} className={`chip chip-state-${s.toLowerCase()} ${stateFilter === s ? "chip-on" : ""}`} onClick={() => setStateFilter(s)}>
            {s === "All" ? "Any state" : s}
          </button>
        ))}
      </div>
    </div>
  );
}

function DeviceTile({ device: d, selected, flagged, onSelect, onCommand, busy }) {
  const isActuator = d.category === "Actuator";
  const unit = UNITS[d.metric] || "";
  const level = d.alert.toLowerCase();

  return (
    <div className={`tile tile-${level} ${selected ? "tile-selected" : ""}`} onClick={onSelect} role="button" tabIndex={0}
         onKeyDown={(e) => { if (e.key === "Enter") onSelect(); }}>
      <div className="tile-top">
        <span className={`state-dot dot-${level}`} />
        <span className="tile-name">{d.name}</span>
        <span className={`state-pill pill-${level}`}>{d.alert}</span>
      </div>
      <div className="tile-loc">{d.location}</div>

      {isActuator ? (
        <div className="actuator">
          <span className={`act-state ${d.actuatorOn ? "act-on" : "act-off"}`}>{d.actuatorOn ? (d.name.toLowerCase().includes("valve") ? "OPEN" : "ON") : (d.name.toLowerCase().includes("valve") ? "CLOSED" : "OFF")}</span>
          <div className="act-buttons" onClick={(e) => e.stopPropagation()}>
            <button disabled={busy || d.actuatorOn} onClick={() => onCommand({ type: "TurnOn", targetMac: d.mac }, `Turn on ${d.name}`)}>On</button>
            <button className="ghost" disabled={busy || !d.actuatorOn} onClick={() => onCommand({ type: "TurnOff", targetMac: d.mac }, `Turn off ${d.name}`)}>Off</button>
          </div>
        </div>
      ) : (
        <div className="tile-value">
          <span className="big">{d.value != null ? d.value.toFixed(d.metric === "wattage" ? 0 : 1) : "-"}</span>
          <span className="unit">{unit}</span>
          <Sparkline values={d.sparkline} metric={d.metric} />
        </div>
      )}

      {d.alertMessage && d.alert !== "Ok" && <div className="tile-msg">{d.alertMessage}</div>}
      {flagged && d.alert === "Ok" && <div className="tile-flag">&#9888; {flagged.title.replace(`${d.name}: `, "")}</div>}

      <div className="tile-foot mono">
        <span>{d.mac}</span>
        <span>{timeAgo(d.secondsSinceSeen)}</span>
      </div>
    </div>
  );
}

function SuggestionsPanel({ suggestions, engine, onRun }) {
  return (
    <section className="card suggest-card">
      <div className="card-head">
        <h3>Suggested Actions</h3>
        <span className="badge-soft">{engine ? `${engine.seededSessions} seeded + ${engine.liveObservations} live obs.` : ""}</span>
      </div>
      {(!suggestions || suggestions.length === 0) && (
        <p className="muted">Nothing to suggest right now. Suggestions appear when an alert fires or after you act, based on what operators usually do next.</p>
      )}
      {(suggestions || []).map((s) => (
        <div key={s.id} className={`suggestion ${s.triggerIsLiveAlert ? "sug-live" : ""}`}>
          <div className="sug-head">
            <strong>{s.title}</strong>
            <button className={s.actionKind === "command" ? "" : "ghost"} onClick={() => onRun(s)}>
              {s.actionKind === "command" ? "Run" : "Open"}
            </button>
          </div>
          <p className="sug-reason">{s.reason}</p>
          <div className="confidence" title={`Confidence ${(s.confidence * 100).toFixed(0)}%`}>
            <div className="conf-bar"><span style={{ width: `${s.confidence * 100}%` }} /></div>
            <span className="mono">{(s.confidence * 100).toFixed(0)}% &middot; seen {s.support}&times;</span>
          </div>
        </div>
      ))}
    </section>
  );
}

function InsightsPanel({ insights, onView }) {
  return (
    <section className="card insight-card">
      <div className="card-head"><h3>Automated Insights</h3></div>
      {(!insights || insights.length === 0) && <p className="muted">All sensors steady. Trend lines are checked every refresh.</p>}
      {(insights || []).map((i) => (
        <div key={i.id} className={`insight ins-${i.severity}`}>
          <div className="ins-head">
            <span className="ins-kind">{i.kind}</span>
            <strong>{i.title}</strong>
          </div>
          <p>{i.detail}</p>
          {i.deviceMac && <button className="link" onClick={() => onView(i.deviceMac)}>View timeline &rarr;</button>}
        </div>
      ))}
    </section>
  );
}

// Timeline graph from the SortedDictionary history. Out-of-band points are
// annotated directly on the chart (the Part 1 research strategy, carried over).
function TimelineCard({ mac, active }) {
  const [range, setRange] = useSessionState("cs.range", 5);
  const [points, setPoints] = useState([]);
  const [device, setDevice] = useState(null);

  // Loads the device on its own (O(1) lookup by MAC) so the timeline still
  // works when the selected device is filtered out of the grid.
  const load = useCallback(async () => {
    if (!mac) return;
    const from = new Date(Date.now() - range * 60_000).toISOString();
    const [h, d] = await Promise.all([
      api(`/api/devices/${encodeURIComponent(mac)}/history?from=${encodeURIComponent(from)}&limit=600`),
      api(`/api/devices/${encodeURIComponent(mac)}`),
    ]);
    if (h.ok) setPoints(h.data);
    if (d.ok) setDevice(d.data);
  }, [mac, range]);

  useEffect(() => { if (active) load(); }, [load, active]);
  useInterval(load, 2000, active);

  const metric = device?.metric;
  return (
    <section className="card timeline-card" id="timeline-card">
      <div className="card-head">
        <h3>Timeline &middot; {device ? device.name : mac}</h3>
        <div className="chip-row">
          {[2, 5, 10].map((m) => (
            <button key={m} className={`chip ${range === m ? "chip-on" : ""}`} onClick={() => setRange(m)}>{m} min</button>
          ))}
        </div>
      </div>
      {device?.category === "Actuator"
        ? <p className="muted">Actuators report a heartbeat rather than a reading - pick a sensor tile to see its timeline.</p>
        : <TimelineChart points={points} metric={metric} />}
      <p className="muted small">Range query on <code>SensorTimeline</code> (a <code>SortedDictionary&lt;DateTime, double&gt;</code> per device): the API skips to <em>from</em> and stops at <em>to</em>, already in time order - no sorting in the browser.</p>
    </section>
  );
}

function TimelineChart({ points, metric }) {
  const W = 640, H = 220, P = { l: 44, r: 12, t: 12, b: 26 };
  if (!points || points.length < 2) return <div className="chart-empty">Waiting for readings...</div>;

  const band = BANDS[metric];
  const unit = UNITS[metric] || "";
  const values = points.map((p) => p.value);
  let lo = Math.min(...values), hi = Math.max(...values);
  if (band) { lo = Math.min(lo, band[0]); hi = Math.max(hi, band[1]); }
  const pad = (hi - lo) * 0.08 || 1;
  lo -= pad; hi += pad;

  const t0 = new Date(points[0].timestamp).getTime();
  const t1 = new Date(points[points.length - 1].timestamp).getTime();
  const x = (t) => P.l + ((new Date(t).getTime() - t0) / Math.max(1, t1 - t0)) * (W - P.l - P.r);
  const y = (v) => P.t + (1 - (v - lo) / (hi - lo)) * (H - P.t - P.b);

  const d = points.map((p, i) => `${i ? "L" : "M"}${x(p.timestamp).toFixed(1)},${y(p.value).toFixed(1)}`).join(" ");
  const outPoints = band ? points.filter((p) => p.value < band[0] || p.value > band[1]) : [];
  // Only label the first point of each breach run so the chart doesn't get cluttered.
  const annotated = outPoints.filter((p, i) => {
    const idx = points.indexOf(p);
    return idx === 0 || !(points[idx - 1].value < band[0] || points[idx - 1].value > band[1]);
  }).slice(-4);

  const ticks = 4;
  const yTicks = Array.from({ length: ticks + 1 }, (_, i) => lo + ((hi - lo) * i) / ticks);
  const xTicks = Array.from({ length: 4 }, (_, i) => t0 + ((t1 - t0) * i) / 3);

  return (
    <svg viewBox={`0 0 ${W} ${H}`} className="timeline" role="img" aria-label={`${metric} timeline`}>
      {yTicks.map((v) => (
        <g key={v}>
          <line x1={P.l} x2={W - P.r} y1={y(v)} y2={y(v)} className="grid-line" />
          <text x={P.l - 6} y={y(v) + 4} className="axis" textAnchor="end">{Math.round(v)}</text>
        </g>
      ))}
      {xTicks.map((t, i) => (
        <text key={t} x={x(t)} y={H - 6} className="axis" textAnchor={i === 0 ? "start" : i === xTicks.length - 1 ? "end" : "middle"}>{clockTime(t)}</text>
      ))}
      {band && (
        <rect x={P.l} width={W - P.l - P.r} y={y(band[1])} height={Math.max(0, y(band[0]) - y(band[1]))} className="band" />
      )}
      {band && <text x={W - P.r - 4} y={y(band[0]) - 4} className="band-label" textAnchor="end">min {band[0]}{unit}</text>}
      <path d={d} className="series" />
      {outPoints.map((p) => <circle key={p.timestamp} cx={x(p.timestamp)} cy={y(p.value)} r="3.5" className="breach" />)}
      {annotated.map((p) => {
        const cx = x(p.timestamp), cy = y(p.value);
        const above = cy > H / 2;
        const tx = Math.min(Math.max(cx, P.l + 60), W - P.r - 60);
        return (
          <g key={`a${p.timestamp}`}>
            <line x1={cx} y1={cy} x2={tx} y2={above ? cy - 26 : cy + 26} className="annot-line" />
            <text x={tx} y={above ? cy - 30 : cy + 38} className="annot" textAnchor="middle">
              {p.value.toFixed(1)}{unit} out of band &middot; {clockTime(p.timestamp)}
            </text>
          </g>
        );
      })}
    </svg>
  );
}

function CommandStackCard({ commands, onUndo, onRedo, onCommand }) {
  const c = commands || { undoStack: [], redoStack: [], log: [], undoDepth: 0, redoDepth: 0 };
  return (
    <section className="card stack-card">
      <div className="card-head">
        <h3>Override history</h3>
        <span className="muted mono">CommandUndoStack &middot; Stack&lt;T&gt;</span>
      </div>
      <div className="stack-actions">
        <button onClick={onUndo} disabled={c.undoDepth === 0}>&#8630; Undo <small>({c.undoDepth})</small></button>
        <button className="ghost" onClick={onRedo} disabled={c.redoDepth === 0}>Redo &#8631; <small>({c.redoDepth})</small></button>
      </div>
      <div className="emergency">
        <button className="danger" onClick={() => onCommand({ type: "ShutDownAllValves" })}>Shut down all valves</button>
        <button className="ghost" onClick={() => onCommand({ type: "OpenAllValves" })}>Open all valves</button>
      </div>

      <div className="stack-visual" aria-label="Undo stack, top first">
        {c.undoStack.length === 0 && <div className="muted">Stack is empty - issue an override to push one.</div>}
        {c.undoStack.slice(0, 6).map((r, i) => (
          <div key={r.id} className={`stack-item ${i === 0 ? "stack-top" : ""}`}>
            {i === 0 && <span className="top-tag">TOP &middot; next undo</span>}
            <strong>#{r.id} {r.label}</strong>
            <span className="mono">{r.issuedBy} &middot; {clockTime(r.issuedAt)}{r.devicesAffected > 1 ? ` · ${r.devicesAffected} devices` : ""}</span>
          </div>
        ))}
      </div>

      <h4>Command log</h4>
      <ul className="cmd-log">
        {c.log.slice(0, 8).map((l, i) => (
          <li key={i}>
            <span className={`log-tag tag-${l.action.toLowerCase()}`}>{l.action}</span>
            <span>{l.label}</span>
            <span className="mono">{l.issuedBy === "Auto-Engine" ? "auto" : ""} {clockTime(l.at)}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}

// Two lanes drawn from the live queue contents + the SSE packet feed.
function PipelineCard({ dash, live }) {
  const q = dash?.queue || { priority: [], standard: [] };
  const s = dash?.stats || {};
  const [rate, setRate] = useState(null);
  const shownRate = rate ?? s.processingRatePerSecond ?? 60;

  async function applyRate(v) {
    setRate(v);
    await api(`/api/stream/rate?perSecond=${v}`, { method: "POST" });
  }

  return (
    <section className="card pipeline-card">
      <div className="card-head">
        <h3>Ingestion pipeline</h3>
        <label className="rate">
          Gateway CPU budget <strong>{shownRate}</strong> pkt/s
          <input type="range" min="5" max="400" step="5" value={shownRate} onChange={(e) => applyRate(Number(e.target.value))} />
        </label>
      </div>

      <div className="lane lane-priority">
        <div className="lane-label">
          <strong>Priority lane</strong>
          <span className="mono">CriticalAlertQueue &middot; {s.priority?.depth ?? 0} waiting</span>
        </div>
        <div className="lane-track">
          {q.priority.length === 0 && <span className="lane-empty">empty - critical packets are served on the next tick</span>}
          {q.priority.map((p) => <span key={p.sequence} className={`pkt pkt-${p.severity.toLowerCase()}`}>{p.deviceName.replace(/^Smart Meter - /, "Meter ")} &middot; {p.value}</span>)}
        </div>
      </div>

      <div className="lane lane-standard">
        <div className="lane-label">
          <strong>Standard lane</strong>
          <span className="mono">TelemetryQueue (FIFO) &middot; {s.standard?.depth ?? 0} waiting</span>
        </div>
        <div className="lane-track">
          {q.standard.length === 0 && <span className="lane-empty">empty - keeping up with the fleet</span>}
          {q.standard.map((p) => <span key={p.sequence} className="pkt">#{p.sequence}</span>)}
          {(s.standard?.depth ?? 0) > q.standard.length && <span className="pkt pkt-more">+{s.standard.depth - q.standard.length}</span>}
        </div>
      </div>

      <h4>Live packets <span className="muted">(Server-Sent Events)</span></h4>
      <div className="feed">
        {live.packets.slice(0, 14).map((p) => (
          <div key={p.sequence} className={`feed-row ${p.lane === "priority" ? "feed-prio" : ""}`}>
            <span className={`lane-tag ${p.lane}`}>{p.lane === "priority" ? "PRIO" : "FIFO"}</span>
            <span className="feed-dev">{p.deviceName}</span>
            <span className="mono">{p.metric} = {p.value}</span>
            <span className="mono feed-lat">{fmtMs(p.latencyMs)}</span>
          </div>
        ))}
        {live.packets.length === 0 && <div className="muted">Waiting for the stream...</div>}
      </div>
    </section>
  );
}

function SimulationCard({ onDone, active }) {
  const toast = useToast();
  const [offlineMac, setOfflineMac] = useState("AA:BB:CC:00:07");
  const [all] = usePoll("/api/devices", 10000, active);
  const sensors = (all || []).filter((d) => d.category !== "Actuator");

  // These POST to /api/telemetry exactly like a device would.
  async function inject(deviceId, metric, rawValue, label) {
    const r = await api("/api/telemetry", { method: "POST", body: JSON.stringify({ deviceId, metric, valueType: "float", rawValue }) });
    if (r.ok) toast(`${label} -> ${r.data.lane} lane (${r.data.severity})`, r.data.lane === "priority" ? "crit" : "info");
    onDone();
  }

  async function burst(count) {
    await api(`/api/simulate/burst?count=${count}`, { method: "POST" });
    toast(`Burst of ${count} normal packets fired at the API`, "info");
    onDone();
  }

  async function burstThenCritical() {
    await burst(900);
    setTimeout(() => inject("AA:BB:CC:00:03", "wattage", "2950", "Severe power spike"), 1800);
  }

  async function offline() {
    await api(`/api/simulate/offline/${encodeURIComponent(offlineMac)}?seconds=30`, { method: "POST" });
    toast("Node will stop transmitting for 30s - watch the Offline set", "warn");
  }

  return (
    <section className="card sim-card">
      <div className="card-head"><h3>Simulate</h3><span className="muted">every packet goes through the HTTP API</span></div>
      <div className="sim-grid">
        <button className="danger" onClick={burstThenCritical}>Burst 900 + critical spike</button>
        <button onClick={() => burst(600)}>Burst 600 normal</button>
        <button className="warn-btn" onClick={() => inject("AA:BB:CC:00:03", "wattage", "2950", "Power spike 2950W")}>Critical power spike</button>
        <button className="warn-btn" onClick={() => inject("AA:BB:CC:00:01", "moisture", "8.5", "Moisture drop 8.5%")}>Critical moisture drop</button>
      </div>
      <div className="sim-offline">
        <select value={offlineMac} onChange={(e) => setOfflineMac(e.target.value)} aria-label="Node to take offline">
          {sensors.map((d) => <option key={d.mac} value={d.mac}>{d.name}</option>)}
        </select>
        <button className="ghost" onClick={offline}>Take offline 30s</button>
      </div>
    </section>
  );
}

function AlertSetCard({ alerts }) {
  const a = alerts || { activeErrors: [], disconnected: [], feed: [] };
  return (
    <section className="card">
      <div className="card-head"><h3>Alert state</h3><span className="muted mono">ActiveErrorStates &middot; DisconnectedNodes</span></div>
      <div className="set-boxes">
        <div>
          <span className="set-title">Active errors ({a.activeErrors.length})</span>
          {a.activeErrors.length === 0 ? <span className="muted">&empty; empty</span>
            : a.activeErrors.map((k) => <code key={k} className="set-item">{k.replace("alert|", "")}</code>)}
        </div>
        <div>
          <span className="set-title">Disconnected ({a.disconnected.length})</span>
          {a.disconnected.length === 0 ? <span className="muted">&empty; empty</span>
            : a.disconnected.map((k) => <code key={k} className="set-item">{k}</code>)}
        </div>
      </div>
      <ul className="alert-feed">
        {a.feed.slice(0, 6).map((e, i) => (
          <li key={i} className={`af-${e.kind}`}>
            <span className="af-kind">{e.kind}</span>
            <span><strong>{e.deviceName}</strong> {e.message}{e.repeat > 1 && <span className="repeat" title="Same event again - grouped instead of listed twice"> &times;{e.repeat}</span>}</span>
            <span className="mono">{clockTime(e.at)}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}

function PatternsCard({ active, engine }) {
  const [patterns] = usePoll("/api/engine/patterns", 5000, active);
  return (
    <section className="card patterns-card">
      <div className="card-head">
        <h3>Learned patterns</h3>
        <span className="muted mono">ActionPatterns</span>
      </div>
      <table>
        <thead><tr><th>When this happens&hellip;</th><th>&hellip;operators do this</th><th>Seen</th><th>Confidence</th></tr></thead>
        <tbody>
          {(patterns || []).slice(0, 8).map((p) => (
            <tr key={p.antecedent + p.consequent}>
              <td>{p.antecedentLabel}</td>
              <td>{p.consequentLabel}</td>
              <td>{p.support}</td>
              <td><div className="conf-bar small"><span style={{ width: `${p.confidence * 100}%` }} /></div></td>
            </tr>
          ))}
        </tbody>
      </table>
      <h4>RecentActivityWindow <span className="muted">(Queue, last 3 min)</span></h4>
      <ul className="activity">
        {(engine?.recentActivity || []).slice(0, 6).map((e, i) => (
          <li key={i}><span className={`act-kind k-${e.kind.toLowerCase()}`}>{e.kind}</span>{e.label}<span className="mono">{clockTime(e.at)}</span></li>
        ))}
      </ul>
    </section>
  );
}

// Live view of SmartXDataStructures - every structure by the name it has in code.
function StructuresCard({ dash }) {
  const named = dash?.named || {};
  const order = [
    ["telemetryQueue", "TelemetryQueue"],
    ["criticalAlertQueue", "CriticalAlertQueue"],
    ["commandUndoStack", "CommandUndoStack"],
    ["commandRedoStack", "CommandRedoStack"],
    ["deviceRegistry", "DeviceRegistry"],
    ["categoryIndex", "CategoryIndex"],
    ["sensorTimeline", "SensorTimeline"],
    ["activeErrorStates", "ActiveErrorStates"],
    ["disconnectedNodes", "DisconnectedNodes"],
    ["actionPatterns", "ActionPatterns"],
    ["recentActivityWindow", "RecentActivityWindow"],
  ];
  return (
    <section className="card">
      <div className="card-head">
        <h3>Data structures in play</h3>
        <span className="muted mono">SmartXDataStructures.cs</span>
      </div>
      <table className="ds-table">
        <thead><tr><th>Name</th><th>Type &amp; purpose</th><th>Size</th></tr></thead>
        <tbody>
          {order.map(([key, name]) => {
            const s = named[key];
            const count = s ? (typeof s.count === "object" ? Object.values(s.count).join(" / ") : s.count) : "-";
            return (
              <tr key={key}>
                <td><strong>{name}</strong></td>
                <td><code>{s?.type}</code><div className="muted small">{s?.purpose}</div></td>
                <td className="mono">{count}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </section>
  );
}
