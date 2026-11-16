// Final PoE - Tab 3: Network Topology & Mesh Routing.
//
//   Mesh map           GET /api/topology            graph + MST edges, polled every 3 s
//   Route tracer       GET /api/topology/route/:q   MST path a node's packets take home
//   Subnet explorer    GET /api/topology/explore/:s BFS: who's connected, who's cut off
//   Fail / restore     POST /api/topology/nodes/:mac/fail|restore
//   Historical log     GET /api/history/:mac        AVL-tree range query
//
// Selections (node, sensor, time window, subnet) are kept in sessionStorage so
// switching tabs never loses them.

const NODE_RADIUS = { Gateway: 22, Router: 15, Sensor: 11, Actuator: 11 };

function TopologyView({ active }) {
  const toast = useToast();
  const [topo, reload] = usePoll("/api/topology", 3000, active);
  const [selected, setSelected] = useSessionState("topo.selected", "AA:BB:CC:00:02");
  const [showAll, setShowAll] = useSessionState("topo.showAll", true);
  const [route, setRoute] = useState(null);

  const nodes = topo?.nodes || [];
  const byMac = useMemo(() => new Map(nodes.map((n) => [n.mac, n])), [nodes]);

  const traceRoute = useCallback(async (query) => {
    if (!query) return;
    const r = await api(`/api/topology/route/${encodeURIComponent(query)}`);
    if (r.ok) {
      setRoute(r.data);
      setSelected(r.data.mac);
    } else {
      toast((r.data && r.data.error) || "No node matches that MAC or name", "warn");
    }
  }, [setSelected, toast]);

  // Re-trace whenever the topology refreshes, so a failure shows up immediately.
  useEffect(() => { if (active && selected) traceRoute(selected); }, [topo, active]); // eslint-disable-line react-hooks/exhaustive-deps

  async function failOrRestore(node) {
    const action = node.status === "failed" ? "restore" : "fail";
    const r = await api(`/api/topology/nodes/${encodeURIComponent(node.mac)}/${action}`, { method: "POST" });
    if (r.ok) toast(action === "fail" ? `${node.name} taken down - watch the mesh re-route` : `${node.name} restored`, action === "fail" ? "crit" : "good");
    else toast((r.data && r.data.error) || "Action failed", "warn");
    reload();
  }

  async function restoreAll() {
    await api("/api/topology/restore-all", { method: "POST" });
    toast("All radios restored", "good");
    reload();
  }

  const sel = byMac.get(selected);

  return (
    <div className="topo">
      <div className="page-head">
        <div>
          <p className="eyebrow">Tab 3 &middot; Final PoE</p>
          <h2>Network Topology &amp; Mesh Routing</h2>
        </div>
        <button className="ghost" onClick={restoreAll} disabled={!topo?.failed?.length}>Restore all radios</button>
      </div>

      <TopologyKpis topo={topo} />

      <div className="cs-main">
        <section className="card">
          <div className="card-head">
            <h3>Mesh map</h3>
            <div className="chip-row">
              <button className={`chip ${showAll ? "chip-on" : ""}`} onClick={() => setShowAll(true)}>All radio links</button>
              <button className={`chip ${!showAll ? "chip-on" : ""}`} onClick={() => setShowAll(false)}>MST only</button>
            </div>
          </div>
          {topo ? (
            <MeshMap topo={topo} byMac={byMac} showAll={showAll} selected={selected} route={route}
                     onSelect={(mac) => traceRoute(mac)} />
          ) : <div className="chart-empty">Loading mesh...</div>}
          <MeshLegend />
        </section>

        <aside className="cs-side">
          <RouteTracer nodes={nodes} route={route} selectedNode={sel} onTrace={traceRoute} onFailRestore={failOrRestore} />
        </aside>
      </div>

      <div className="cs-row">
        <SubnetExplorer active={active} topo={topo} onSelect={traceRoute} />
        <MstCard topo={topo} />
      </div>

      <HistoryLog active={active} selectedMac={selected} />
    </div>
  );
}

// ---------------------------------------------------------------------------

function TopologyKpis({ topo }) {
  const s = topo?.stats || {};
  const m = topo?.mst || {};
  const saving = m.allActiveLinksCost ? Math.round((1 - m.totalCost / m.allActiveLinksCost) * 100) : null;
  const tiles = [
    { label: "Radios (vertices)", value: s.vertices ?? "-", sub: `${s.edges ?? "-"} wireless links (edges)`, tone: "" },
    { label: "Online", value: s.online ?? "-", sub: "routed to the gateway", tone: "good" },
    { label: "Down", value: s.down ?? "-", sub: "failed or no heartbeat", tone: s.down ? "crit" : "good" },
    { label: "Isolated", value: s.isolated ?? "-", sub: "no path left", tone: s.isolated ? "warn" : "good" },
    { label: "Re-routed", value: s.rerouted ?? "-", sub: "self-healed paths", tone: s.rerouted ? "prio" : "" },
    { label: "MST energy", value: m.totalCost ?? "-", unit: "mJ", sub: saving != null ? `${saving}% less than using every link` : "", tone: "" },
  ];
  return (
    <div className="kpis">
      {tiles.map((t) => (
        <div key={t.label} className={`kpi kpi-${t.tone}`}>
          <span className="kpi-label">{t.label}</span>
          <span className="kpi-value">{t.value}{t.unit && <small>{t.unit}</small>}</span>
          <span className="kpi-sub">{t.sub}</span>
        </div>
      ))}
    </div>
  );
}

function MeshMap({ topo, byMac, showAll, selected, route, onSelect }) {
  const { width, height } = topo.canvas;
  const routePairs = useMemo(() => {
    const set = new Set();
    const path = route?.path || [];
    for (let i = 0; i + 1 < path.length; i++) set.add([path[i], path[i + 1]].sort().join("|"));
    return set;
  }, [route]);

  const links = topo.links.filter((l) => showAll || l.inMst);
  const routePoints = (route?.path || []).map((m) => byMac.get(m)).filter(Boolean);

  return (
    <svg viewBox={`0 0 ${width} ${height}`} className="mesh-map" role="img" aria-label="Mesh network graph">
      <defs>
        <radialGradient id="topo-glow" cx="50%" cy="50%" r="50%">
          <stop offset="0" stopColor="#4f8cff" stopOpacity="0.22" />
          <stop offset="1" stopColor="#4f8cff" stopOpacity="0" />
        </radialGradient>
      </defs>
      <circle cx={500} cy={320} r={260} fill="url(#topo-glow)" />

      {/* subnet labels near each router */}
      {topo.nodes.filter((n) => n.kind === "Router").map((n) => (
        <text key={`sub-${n.mac}`} x={n.x} y={n.y - 26} textAnchor="middle" className="subnet-label">{n.subnet.toUpperCase()}</text>
      ))}

      {links.map((l) => {
        const a = byMac.get(l.a), b = byMac.get(l.b);
        if (!a || !b) return null;
        const onRoute = routePairs.has([l.a, l.b].sort().join("|"));
        const cls = !l.active ? "edge edge-dead" : l.inMst ? "edge edge-mst" : "edge edge-alt";
        return (
          <g key={`${l.a}-${l.b}`}>
            <line x1={a.x} y1={a.y} x2={b.x} y2={b.y} className={onRoute ? `${cls} edge-route` : cls}>
              <title>{`${a.name} ↔ ${b.name}\n${l.powerCost} mJ/packet · RSSI ${l.rssi} dBm · ${l.distance} m`}</title>
            </line>
            {onRoute && (
              <text x={(a.x + b.x) / 2} y={(a.y + b.y) / 2 - 6} textAnchor="middle" className="edge-cost">{l.powerCost} mJ</text>
            )}
          </g>
        );
      })}

      {routePoints.length > 1 && (
        <polyline points={routePoints.map((n) => `${n.x},${n.y}`).join(" ")} className="route-flow" />
      )}

      {topo.nodes.map((n) => {
        const r = NODE_RADIUS[n.kind] || 11;
        const isSel = n.mac === selected;
        return (
          <g key={n.mac} transform={`translate(${n.x},${n.y})`} className={`mnode mnode-${n.status}`} onClick={() => onSelect(n.mac)}
             role="button" tabIndex={0} onKeyDown={(e) => { if (e.key === "Enter") onSelect(n.mac); }}>
            <title>{`${n.name} (${n.mac})\n${n.status}${n.hops != null ? ` · ${n.hops} hops to gateway` : ""}${n.value != null ? `\n${n.metric}: ${n.value}` : ""}`}</title>
            {isSel && <circle r={r + 9} className="mnode-sel" />}
            {n.rerouted && <circle r={r + 5} className="mnode-rerouted" />}
            {n.kind === "Router" || n.kind === "Actuator"
              ? <rect x={-r} y={-r} width={r * 2} height={r * 2} rx={n.kind === "Router" ? 4 : 3} className="mnode-shape" transform={n.kind === "Router" ? "rotate(45)" : undefined} />
              : <circle r={r} className="mnode-shape" />}
            {n.kind === "Gateway" && <path d="M-8 3 a10 10 0 0 1 16 0 M-12 -2 a16 16 0 0 1 24 0 M-4 8 a4 4 0 0 1 8 0" className="gw-wifi" />}
            {(n.status === "failed" || n.status === "offline") && <path d={`M${-r / 1.6} ${-r / 1.6} L${r / 1.6} ${r / 1.6} M${r / 1.6} ${-r / 1.6} L${-r / 1.6} ${r / 1.6}`} className="mnode-x" />}
            <text y={r + 14} textAnchor="middle" className="mnode-label">{n.name.replace("Smart Meter - ", "Meter ").replace(" Mesh Router", " Router")}</text>
          </g>
        );
      })}
    </svg>
  );
}

function MeshLegend() {
  return (
    <div className="mesh-legend">
      <span><i className="lg lg-gw" /> Gateway</span>
      <span><i className="lg lg-router" /> Mesh router</span>
      <span><i className="lg lg-sensor" /> Sensor</span>
      <span><i className="lg lg-act" /> Actuator</span>
      <span><i className="lg-line lg-mst" /> MST link</span>
      <span><i className="lg-line lg-alt" /> Backup link</span>
      <span><i className="lg-line lg-route" /> Traced route</span>
      <span><i className="lg lg-down" /> Down</span>
      <span><i className="lg lg-iso" /> Isolated</span>
      <span><i className="lg lg-rer" /> Re-routed</span>
    </div>
  );
}

function RouteTracer({ nodes, route, selectedNode, onTrace, onFailRestore }) {
  const [query, setQuery] = useState("");
  const statusTone = { online: "ok", failed: "critical", offline: "offline", isolated: "warning" };

  return (
    <section className="card route-card">
      <div className="card-head"><h3>Route tracer</h3><span className="muted mono">Prim's MST path</span></div>
      <form className="trace-form" onSubmit={(e) => { e.preventDefault(); onTrace(query.trim()); }}>
        <input list="mesh-nodes" value={query} onChange={(e) => setQuery(e.target.value)}
               placeholder="MAC or name, e.g. AA:BB:CC:00:02" aria-label="Node MAC address or name" />
        <datalist id="mesh-nodes">
          {nodes.map((n) => <option key={n.mac} value={n.mac}>{n.name}</option>)}
        </datalist>
        <button type="submit">Trace</button>
      </form>

      {!route && <p className="muted">Click a node on the map or enter a MAC address to see how its packets hop to the gateway.</p>}

      {route && (
        <div className="route-body">
          <div className="route-title">
            <div>
              <strong>{route.name}</strong>
              <div className="mono">{route.mac} &middot; {route.subnet} &middot; {route.kind}</div>
            </div>
            <span className={`state-pill pill-${statusTone[route.status] || "ok"}`}>{route.status}</span>
          </div>

          {route.path.length > 0 ? (
            <>
              <div className="route-summary">
                <span><strong>{route.hopCount}</strong> hops</span>
                <span><strong>{route.totalCost}</strong> mJ / packet</span>
                {route.rerouted && <span className="badge-rer">re-routed (self-healed)</span>}
              </div>
              <ol className="hop-list">
                {route.hops.map((h, i) => (
                  <li key={i}>
                    <span className="hop-n">{i + 1}</span>
                    <span className="hop-names">{h.fromName} <em>&rarr;</em> {h.toName}</span>
                    <span className="mono">{h.powerCost} mJ &middot; {h.rssi} dBm</span>
                  </li>
                ))}
                {route.hops.length === 0 && <li className="muted">This is the central gateway.</li>}
              </ol>
              {route.fewestHopPath.length > 0 && route.fewestHopPath.length !== route.path.length && (
                <p className="muted small">
                  A BFS (fewest hops) route would take {route.fewestHopPath.length - 1} hops, but uses longer, more power-hungry links.
                  The MST trades a hop or two for lower transmit energy.
                </p>
              )}
            </>
          ) : (
            <div className="no-route">
              <strong>{route.status === "isolated" ? "No route to the gateway" : "This radio is down"}</strong>
              <p className="muted small">Neighbours it could hop through:</p>
              <ul>
                {route.neighbours.map((n) => (
                  <li key={n.mac}><span>{n.name}</span><span className={`nb-${n.state.replace(" ", "-")}`}>{n.state}{n.hopsToGateway != null ? ` · ${n.hopsToGateway} hops` : ""}</span></li>
                ))}
              </ul>
            </div>
          )}

          {selectedNode && selectedNode.kind !== "Gateway" && (
            <button className={selectedNode.status === "failed" ? "" : "danger"} onClick={() => onFailRestore(selectedNode)}>
              {selectedNode.status === "failed" ? "Restore this radio" : "Simulate failure of this radio"}
            </button>
          )}
        </div>
      )}
    </section>
  );
}

function SubnetExplorer({ active, topo, onSelect }) {
  const [start, setStart] = useSessionState("topo.bfsStart", "AA:BB:CC:FF:00");
  const [subnetOnly, setSubnetOnly] = useSessionState("topo.subnetOnly", false);
  const [result, setResult] = useState(null);

  const roots = (topo?.nodes || []).filter((n) => n.kind === "Gateway" || n.kind === "Router");

  const run = useCallback(async () => {
    const r = await api(`/api/topology/explore/${encodeURIComponent(start)}?subnetOnly=${subnetOnly}`);
    if (r.ok) setResult(r.data);
  }, [start, subnetOnly]);

  useEffect(() => { if (active) run(); }, [run, active, topo]);

  return (
    <section className="card">
      <div className="card-head">
        <h3>Subnet explorer</h3>
        <span className="muted mono">Breadth-First Search</span>
      </div>
      <div className="bfs-controls">
        <select value={start} onChange={(e) => setStart(e.target.value)} aria-label="Start BFS from">
          {roots.map((n) => <option key={n.mac} value={n.mac}>{n.name}</option>)}
        </select>
        <label className="inline-check">
          <input type="checkbox" checked={subnetOnly} onChange={(e) => setSubnetOnly(e.target.checked)} />
          Stay inside this subnet
        </label>
      </div>

      {result && (
        <>
          <p className="muted small">
            {result.startDown
              ? <>The start radio is <strong>down</strong>, so nothing can be reached through it.</>
              : <>Reached <strong>{result.reachedCount}</strong> of {result.scopeCount} radios in <strong>{result.subnet}</strong>, hop by hop:</>}
          </p>
          <div className="bfs-levels">
            {result.levels.map((lv) => (
              <div key={lv.hop} className="bfs-level">
                <span className="bfs-hop">{lv.hop === 0 ? "start" : `${lv.hop} hop${lv.hop > 1 ? "s" : ""}`}</span>
                <div className="bfs-nodes">{lv.nodes.map((n) => <span key={n} className="bfs-node">{n}</span>)}</div>
              </div>
            ))}
          </div>

          {result.cutOff.length > 0 && (
            <>
              <h4>Not reached by this search</h4>
              <ul className="cutoff">
                {result.cutOff.map((c) => (
                  <li key={c.mac} className={`co-${c.state}`}>
                    <div className="co-head">
                      <button className="link" onClick={() => onSelect(c.mac)}>{c.name}</button>
                      <span className={`co-state co-${c.state}`}>{c.state}</span>
                    </div>
                    <span className="muted small">{c.reason}</span>
                    {c.dependants.length > 0 && (
                      <ul className="dependants">
                        {c.dependants.map((d) => (
                          <li key={d.mac}>{d.name}: {d.newNextHop ? <>now hops via <strong>{d.newNextHop}</strong> ({d.hopsNow} hops)</> : <strong>no alternative path</strong>}</li>
                        ))}
                      </ul>
                    )}
                  </li>
                ))}
              </ul>
            </>
          )}
        </>
      )}
    </section>
  );
}

function MstCard({ topo }) {
  const m = topo?.mst;
  const max = m ? Math.max(m.allActiveLinksCost, m.baselineCost || 0, m.totalCost) : 1;
  const bars = m ? [
    { label: "Every radio link in use", value: m.allActiveLinksCost, cls: "bar-all" },
    { label: "MST, all radios healthy", value: m.baselineCost, cls: "bar-base" },
    { label: "MST right now", value: m.totalCost, cls: "bar-now" },
  ] : [];
  return (
    <section className="card">
      <div className="card-head"><h3>Power-efficient backbone</h3><span className="muted mono">Prim + MinHeap</span></div>
      <p className="muted small">
        Prim's algorithm grows a tree from the central gateway, always taking the cheapest link (lowest transmit energy)
        that reaches a new radio. The MinHeap hands over that cheapest link in O(log E).
      </p>
      <div className="mst-bars">
        {bars.map((b) => (
          <div key={b.label} className="mst-bar">
            <span>{b.label}</span>
            <div className="bar-track"><i className={b.cls} style={{ width: `${(b.value / max) * 100}%` }} /></div>
            <span className="mono">{b.value} mJ</span>
          </div>
        ))}
      </div>
      {m && (
        <table className="ds-table">
          <tbody>
            <tr><td>Tree edges</td><td className="mono">{m.edgeCount} (V &minus; 1 for the connected radios)</td></tr>
            <tr><td>Heap pushes</td><td className="mono">{m.heapPushes}</td></tr>
            <tr><td>Graph storage</td><td className="mono">Adjacency list, O(V + E)</td></tr>
          </tbody>
        </table>
      )}
    </section>
  );
}

// ---------------------------------------------------------------------------
// Historical log - AVL tree range queries
// ---------------------------------------------------------------------------

function toLocalInput(date) {
  const d = new Date(date);
  const pad = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function presetRange(kind) {
  const now = new Date();
  if (kind === "14-16") {
    const day = new Date(now);
    if (now.getHours() < 16) day.setDate(day.getDate() - 1);   // most recent full 14:00-16:00 window
    const from = new Date(day); from.setHours(14, 0, 0, 0);
    const to = new Date(day); to.setHours(16, 0, 0, 0);
    return [from, to];
  }
  const hours = { "1h": 1, "6h": 6, "24h": 24 }[kind];
  return [new Date(now - hours * 3600_000), now];
}

function HistoryLog({ active, selectedMac }) {
  const [overview] = usePoll("/api/history", 15000, active);
  const [mac, setMac] = useSessionState("topo.histMac", "AA:BB:CC:00:01");
  const [range, setRange] = useSessionState("topo.histRange", presetRange("14-16").map(toLocalInput));
  const [result, setResult] = useState(null);
  const [bench, setBench] = useState(null);
  const [page, setPage] = useState(0);
  const [busy, setBusy] = useState(false);
  const PAGE = 14;

  const sensors = overview || [];

  // Follow the node picked on the map when it has history.
  useEffect(() => {
    if (selectedMac && sensors.some((s) => s.mac === selectedMac)) setMac(selectedMac);
  }, [selectedMac, overview]); // eslint-disable-line react-hooks/exhaustive-deps

  const run = useCallback(async () => {
    if (!mac) return;
    setBusy(true);
    const from = new Date(range[0]).toISOString();
    const to = new Date(range[1]).toISOString();
    const r = await api(`/api/history/${encodeURIComponent(mac)}?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}&limit=20000`);
    setBusy(false);
    if (r.ok) { setResult(r.data); setPage(0); setBench(null); }
  }, [mac, range]);

  useEffect(() => { if (active) run(); }, [mac, active]); // eslint-disable-line react-hooks/exhaustive-deps

  async function benchmark() {
    const from = new Date(range[0]).toISOString();
    const to = new Date(range[1]).toISOString();
    const r = await api(`/api/history/${encodeURIComponent(mac)}/benchmark?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`);
    if (r.ok) setBench(r.data);
  }

  function applyPreset(kind) {
    setRange(presetRange(kind).map(toLocalInput));
  }

  useEffect(() => { if (active) run(); }, [range]); // eslint-disable-line react-hooks/exhaustive-deps

  function downloadCsv() {
    if (!result) return;
    const rows = ["timestamp,value"].concat(result.points.map((p) => `${p.timestamp},${p.value}`));
    const blob = new Blob([rows.join("\n")], { type: "text/csv" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = `${result.name.replace(/\W+/g, "_")}_${range[0].replace(/\W/g, "")}.csv`;
    a.click();
    URL.revokeObjectURL(a.href);
  }

  const unit = result ? (UNITS[result.metric] || "") : "";
  const band = result ? BANDS[result.metric] : null;
  const pageRows = result ? result.points.slice(page * PAGE, page * PAGE + PAGE) : [];
  const pages = result ? Math.ceil(result.points.length / PAGE) : 0;

  // Chart: thin very long ranges to ~600 points so the SVG stays light.
  const chartPoints = useMemo(() => {
    if (!result) return [];
    const step = Math.max(1, Math.ceil(result.points.length / 600));
    return result.points.filter((_, i) => i % step === 0);
  }, [result]);

  return (
    <section className="card history-card">
      <div className="card-head">
        <h3>Historical telemetry log</h3>
        <span className="muted mono">AVL tree range query &middot; O(log n + k)</span>
      </div>

      <div className="hist-controls">
        <select value={mac} onChange={(e) => setMac(e.target.value)} aria-label="Sensor">
          {sensors.map((s) => <option key={s.mac} value={s.mac}>{s.name} ({s.mac})</option>)}
        </select>
        <label>From <input type="datetime-local" value={range[0]} onChange={(e) => setRange([e.target.value, range[1]])} /></label>
        <label>To <input type="datetime-local" value={range[1]} onChange={(e) => setRange([range[0], e.target.value])} /></label>
        <div className="chip-row">
          <button className="chip" onClick={() => applyPreset("14-16")}>14:00&ndash;16:00</button>
          <button className="chip" onClick={() => applyPreset("1h")}>Last hour</button>
          <button className="chip" onClick={() => applyPreset("6h")}>Last 6 h</button>
          <button className="chip" onClick={() => applyPreset("24h")}>Last 24 h</button>
        </div>
      </div>

      {busy && !result && <div className="chart-empty">Querying...</div>}

      {result && (
        <>
          <div className="hist-stats">
            <div><span>Readings in range</span><strong>{result.totalInRange.toLocaleString()}</strong></div>
            <div><span>Min / avg / max</span><strong>{result.min ?? "-"} / {result.average ?? "-"} / {result.max ?? "-"} {unit}</strong></div>
            <div><span>Tree size &middot; height</span><strong>{result.stats.treeSize.toLocaleString()} &middot; {result.stats.treeHeight}</strong></div>
            <div><span>Nodes visited</span><strong>{result.stats.nodesVisited.toLocaleString()}</strong></div>
            <div><span>Query time</span><strong>{result.stats.elapsedMicroseconds} &micro;s</strong></div>
            <div className="hist-actions">
              <button className="ghost" onClick={benchmark}>Compare with list scan</button>
              <button className="ghost" onClick={downloadCsv} disabled={!result.points.length}>CSV</button>
            </div>
          </div>

          {bench && (
            <div className="bench">
              AVL visited <strong>{bench.avlNodesVisited.toLocaleString()}</strong> nodes in <strong>{bench.avlMicroseconds} &micro;s</strong>;
              a scan of an unsorted list checked <strong>{bench.listItemsScanned.toLocaleString()}</strong> items in <strong>{bench.listMicroseconds} &micro;s</strong>
              {bench.speedup > 0 && <> &mdash; <strong>{bench.speedup}&times; faster</strong></>}.
              {bench.treeIsValidAvl && <span className="ok-tag">tree verified balanced &amp; ordered</span>}
            </div>
          )}

          <div className="hist-body">
          <div className="hist-chart">
            {result.points.length > 1 ? <TimelineChart points={chartPoints} metric={result.metric} /> : <div className="chart-empty">No readings in this window.</div>}
          </div>
          <div className="hist-log">
          <table className="hist-table">
            <thead><tr><th>Time</th><th>Date</th><th>Reading</th><th>Status</th></tr></thead>
            <tbody>
              {pageRows.map((p) => {
                const out = band && (p.value < band[0] || p.value > band[1]);
                const d = new Date(p.timestamp);
                return (
                  <tr key={p.timestamp} className={out ? "row-out" : ""}>
                    <td className="mono">{d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" })}</td>
                    <td className="mono">{d.toLocaleDateString()}</td>
                    <td>{p.value} {unit}</td>
                    <td>{out ? <span className="state-pill pill-critical">out of band</span> : <span className="state-pill pill-ok">ok</span>}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
          {pages > 1 && (
            <div className="pager">
              <button className="ghost" disabled={page === 0} onClick={() => setPage(page - 1)}>&larr; Prev</button>
              <span className="mono">Page {page + 1} of {pages}</span>
              <button className="ghost" disabled={page >= pages - 1} onClick={() => setPage(page + 1)}>Next &rarr;</button>
            </div>
          )}
          </div>
          </div>
        </>
      )}
    </section>
  );
}
