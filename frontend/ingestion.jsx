// Part 1 - Sensor Data Ingestion & Telemetry.
// Moved out of app.jsx in Part 2 so each pillar lives in its own file.
// Polling now pauses while the view is hidden (the `active` prop) and all
// calls go through the shared api() helper so a dead API can't crash the page.

function RegisterSensorForm({ onRegistered }) {
  const [mac, setMac] = useState("");
  const [name, setName] = useState("");
  const [location, setLocation] = useState("");
  const [category, setCategory] = useState("Environmental");
  const [status, setStatus] = useState(null);

  async function submit(e) {
    e.preventDefault();
    const res = await api("/api/sensors", {
      method: "POST",
      body: JSON.stringify({ macAddress: mac, name: name || null, location, category: categoryToInt(category) }),
    });
    if (res.ok) {
      setStatus("Registered.");
      setMac("");
      setName("");
      setLocation("");
      onRegistered();
    } else {
      setStatus("Could not register that sensor.");
    }
  }

  function categoryToInt(name) {
    return { Environmental: 0, PowerConsumption: 1, Actuator: 2 }[name];
  }

  return (
    <form className="card" onSubmit={submit}>
      <h3>Register a sensor</h3>
      <label>MAC / Unique ID</label>
      <input value={mac} onChange={(e) => setMac(e.target.value)} placeholder="AA:BB:CC:00:05" required />
      <label>Display name (optional)</label>
      <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Moisture Sensor D" />
      <label>Deployment location</label>
      <input value={location} onChange={(e) => setLocation(e.target.value)} placeholder="Zone 1 / Node C" required />
      <label>Category</label>
      <select value={category} onChange={(e) => setCategory(e.target.value)}>
        <option>Environmental</option>
        <option>PowerConsumption</option>
        <option>Actuator</option>
      </select>
      <button type="submit">Register</button>
      {status && <p className="status">{status}</p>}
    </form>
  );
}

function AttachmentUploader({ mac }) {
  const [file, setFile] = useState(null);
  const [uploaded, setUploaded] = useState([]);
  const [busy, setBusy] = useState(false);

  async function upload() {
    if (!file) return;
    setBusy(true);
    const form = new FormData();
    form.append("file", file);
    const res = await api(`/api/sensors/${encodeURIComponent(mac)}/attachments`, {
      method: "POST",
      body: form,
    });
    setBusy(false);
    if (res.ok) {
      setUploaded((prev) => [...prev, res.data.storedName]);
      setFile(null);
    }
  }

  return (
    <div className="uploader">
      <input type="file" onChange={(e) => setFile(e.target.files[0])} />
      <button disabled={!file || busy} onClick={upload}>{busy ? "Uploading..." : "Attach"}</button>
      {uploaded.length > 0 && (
        <ul className="attachment-list">
          {uploaded.map((f) => <li key={f}>{f}</li>)}
        </ul>
      )}
    </div>
  );
}

function SensorList({ refreshKey, active }) {
  const [sensors, setSensors] = useState([]);

  const load = useCallback(async () => {
    const res = await api("/api/sensors");
    if (res.ok) setSensors(res.data);
  }, []);

  useEffect(() => { load(); }, [load, refreshKey]);
  useInterval(load, 4000, active);

  return (
    <div className="card">
      <h3>Registered sensors ({sensors.length})</h3>
      {sensors.map((s) => (
        <div key={s.macAddress} className="sensor-row">
          <div>
            <strong>{s.name || s.location}</strong>
            <div className="mono">{s.macAddress} &middot; {categoryName(s.category)} &middot; {s.location}</div>
          </div>
          <AttachmentUploader mac={s.macAddress} />
        </div>
      ))}
      {sensors.length === 0 && <p className="muted">No sensors registered yet.</p>}
    </div>
  );
}

// Contextual anomaly annotation - the engagement strategy from the research report.
// Each breach is rendered as an annotated card anchored to the metric that
// caused it, with a plain-language explanation, instead of a generic alert list.
function AnomalyFeed({ active }) {
  const [events, setEvents] = useState([]);

  const load = useCallback(async () => {
    const res = await api("/api/anomalies");
    if (res.ok) setEvents(res.data);
  }, []);

  useInterval(load, 2000, active);
  useEffect(() => { load(); }, [load]);

  return (
    <div className="card">
      <h3>Live anomaly annotations</h3>
      {events.length === 0 && <p className="muted">No anomalies detected in the current window - all sensors within band.</p>}
      {events.slice(0, 8).map((e, i) => (
        <div key={i} className="annotation">
          <span className="annotation-dot" />
          <div>
            <strong>{e.deviceId}</strong> &middot; {e.metric} = {e.value}
            <div className="annotation-message">{e.message}</div>
            <div className="annotation-time">{new Date(e.timestamp).toLocaleTimeString()}</div>
          </div>
        </div>
      ))}
    </div>
  );
}

function LiveTelemetryPanel({ active }) {
  const [snapshot, setSnapshot] = useState(null);

  const load = useCallback(async () => {
    const res = await api("/api/telemetry/snapshot");
    if (res.ok) setSnapshot(res.data);
  }, []);

  useInterval(load, 2000, active);
  useEffect(() => { load(); }, [load]);

  if (!snapshot) return <div className="card">Loading telemetry...</div>;

  return (
    <div className="card">
      <h3>Typed packet log</h3>
      <div className="counts">
        <span>float packets: {snapshot.floatCount}</span>
        <span>int packets: {snapshot.intCount}</span>
        <span>bool packets: {snapshot.boolCount}</span>
        <span>raw batch rows: {snapshot.rawBatchRows}</span>
      </div>
      <table>
        <thead><tr><th>Device</th><th>Metric</th><th>Value</th><th>Time</th></tr></thead>
        <tbody>
          {snapshot.recentFloats.slice(-8).reverse().map((r, i) => (
            <tr key={i}><td>{r.deviceId}</td><td>{r.metric}</td><td>{r.value}</td><td>{new Date(r.timestamp).toLocaleTimeString()}</td></tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function RecursionDemo() {
  const [result, setResult] = useState(null);

  async function run() {
    const seedRes = await api("/api/deployment/seed");
    if (!seedRes.ok) return;
    const valRes = await api("/api/deployment/validate", {
      method: "POST",
      body: JSON.stringify(seedRes.data),
    });
    if (valRes.ok) setResult(valRes.data);
  }

  return (
    <div className="card">
      <h3>Deployment tree validation (recursion)</h3>
      <p className="muted">Validates Facility A -&gt; Zone -&gt; Sub-Zone -&gt; Node against duplicate MACs and depth.</p>
      <button onClick={run}>Run validation</button>
      {result && (
        result.length === 0
          ? <p className="status">No issues found - tree is safely configured.</p>
          : <ul>{result.map((r, i) => <li key={i}>{r.path}: {r.message}</li>)}</ul>
      )}
    </div>
  );
}

function OperatorOverloadDemo() {
  const [data, setData] = useState(null);

  async function run() {
    const res = await api("/api/demo/power-aggregate");
    if (res.ok) setData(res.data);
  }

  return (
    <div className="card">
      <h3>Meter aggregation (operator overloading)</h3>
      <button onClick={run}>Combine two meters</button>
      {data && (
        <div className="mono">
          <div>{data.meter1}</div>
          <div>{data.meter2}</div>
          <div>combined = {data.combined}</div>
          <div>delta = {data.delta}</div>
          <div>meter2 &gt; meter1: {String(data.meter2ExceedsMeter1)}</div>
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Bulk device import: upload a .txt list -> the API parses it, registers every
// device and the simulator starts sending their telemetry. The grid, gauges
// and the Command Stream all pick the new devices up on their next refresh.
// ---------------------------------------------------------------------------
function DeviceImportCard({ onImported }) {
  const toast = useToast();
  const [file, setFile] = useState(null);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState(null);
  const [error, setError] = useState(null);
  const [dragging, setDragging] = useState(false);
  const inputRef = useRef(null);

  function pick(f) {
    setResult(null);
    setError(null);
    if (!f) return;
    if (!/\.(txt|csv)$/i.test(f.name)) { setError("Choose a .txt device list."); return; }
    setFile(f);
  }

  async function upload() {
    if (!file) return;
    setBusy(true);
    setError(null);
    const form = new FormData();
    form.append("file", file);
    const r = await api("/api/devices/import", { method: "POST", body: form });
    setBusy(false);
    if (!r.ok) { setError((r.data && r.data.error) || "Import failed."); return; }
    setResult(r.data);
    setFile(null);
    if (inputRef.current) inputRef.current.value = "";
    toast(`Imported ${r.data.imported} new device${r.data.imported === 1 ? "" : "s"}${r.data.updated ? `, updated ${r.data.updated}` : ""}`, "good");
    onImported();
  }

  return (
    <div className="card import-card">
      <h3>Import device list</h3>
      <p className="muted">Upload a <code>.txt</code> file with one device per line: <code>MAC, Name, Location, Category</code>.</p>
      <label
        className={`dropzone ${dragging ? "drag" : ""}`}
        onDragOver={(e) => { e.preventDefault(); setDragging(true); }}
        onDragLeave={() => setDragging(false)}
        onDrop={(e) => { e.preventDefault(); setDragging(false); pick(e.dataTransfer.files[0]); }}
      >
        <input ref={inputRef} type="file" accept=".txt,.csv,text/plain" onChange={(e) => pick(e.target.files[0])} />
        <svg width="26" height="26" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true"><path d="M12 16V4M7 9l5-5 5 5M4 16v4h16v-4" /></svg>
        <span>{file ? <strong>{file.name}</strong> : <>Drop a file here or <u>browse</u></>}</span>
        {file && <span className="muted small">{(file.size / 1024).toFixed(1)} KB</span>}
      </label>
      <div className="import-actions">
        <button disabled={!file || busy} onClick={upload}>{busy ? "Importing..." : "Import devices"}</button>
        <a className="link" href="assets/sample-devices.txt" download>Download sample list</a>
      </div>
      {error && <p className="auth-error">{error}</p>}
      {result && (
        <div className="import-result">
          <div className="import-stats">
            <span><strong>{result.imported}</strong> added</span>
            <span><strong>{result.updated}</strong> updated</span>
            <span><strong>{result.skipped.length}</strong> skipped</span>
          </div>
          <ul className="import-list">
            {result.devices.slice(0, 8).map((d) => (
              <li key={d.macAddress}><span className={`cat-dot cat-${categoryName(d.category)}`} />{d.name}<span className="mono">{d.macAddress}</span></li>
            ))}
            {result.devices.length > 8 && <li className="muted">+{result.devices.length - 8} more</li>}
          </ul>
          {result.skipped.length > 0 && (
            <ul className="import-skipped">
              {result.skipped.map((s) => <li key={s.line}>Line {s.line}: {s.reason}</li>)}
            </ul>
          )}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Live telemetry gauges - one per sensor, coloured by its operating band, plus
// status lights for every actuator. Polls the live device dictionary.
// ---------------------------------------------------------------------------
const GAUGE_SCALE = { moisture: [0, 100], temperature: [-10, 60], wattage: [0, 3000] };

function Gauge({ value, metric }) {
  const [min, max] = GAUGE_SCALE[metric] || [0, 100];
  const band = BANDS[metric];
  const R = 46, CX = 60, CY = 58;
  const frac = (v) => Math.min(1, Math.max(0, (v - min) / (max - min)));
  const point = (f) => {
    const a = Math.PI * (1 - f);
    return [CX + R * Math.cos(a), CY - R * Math.sin(a)];
  };
  const arc = (f0, f1) => {
    const [x0, y0] = point(f0), [x1, y1] = point(f1);
    return `M${x0.toFixed(1)},${y0.toFixed(1)} A${R},${R} 0 0 1 ${x1.toFixed(1)},${y1.toFixed(1)}`;
  };
  const out = band && value != null && (value < band[0] || value > band[1]);
  const f = value == null ? 0 : frac(value);
  const [nx, ny] = point(f);
  return (
    <svg viewBox="0 0 120 70" className="gauge" aria-hidden="true">
      <path d={arc(0, 1)} className="g-track" />
      {band && <path d={arc(frac(band[0]), frac(band[1]))} className="g-band" />}
      {value != null && <path d={arc(0, Math.max(0.001, f))} className={out ? "g-value g-out" : "g-value"} />}
      {value != null && <circle cx={nx} cy={ny} r="4" className={out ? "g-needle g-out" : "g-needle"} />}
    </svg>
  );
}

function LiveGauges({ active }) {
  const [devices] = usePoll("/api/devices", 2000, active);
  const sensors = (devices || []).filter((d) => d.category !== "Actuator");
  const actuators = (devices || []).filter((d) => d.category === "Actuator");

  return (
    <div className="card gauges-card">
      <div className="card-head">
        <h3>Live telemetry</h3>
        <span className="muted">{sensors.length} sensors &middot; {actuators.length} actuators &middot; refreshes every 2s</span>
      </div>
      <div className="gauge-grid">
        {sensors.map((d) => {
          const unit = UNITS[d.metric] || "";
          const level = d.alert.toLowerCase();
          return (
            <div key={d.mac} className={`gauge-tile gt-${level}`}>
              <Gauge value={d.value} metric={d.metric} />
              <div className="gauge-value">
                {d.value != null ? d.value.toFixed(d.metric === "wattage" ? 0 : 1) : "-"}<small>{unit}</small>
              </div>
              <div className="gauge-name" title={d.location}>{d.name}</div>
              <div className="gauge-meta">
                <span className={`state-pill pill-${level}`}>{d.alert}</span>
                <span className="mono">{timeAgo(d.secondsSinceSeen)}</span>
              </div>
              <Sparkline values={d.sparkline} metric={d.metric} width={140} height={24} />
            </div>
          );
        })}
        {sensors.length === 0 && <p className="muted">Waiting for sensors to report...</p>}
      </div>
      {actuators.length > 0 && (
        <div className="lights">
          {actuators.map((d) => (
            <span key={d.mac} className={`light ${d.alert === "Offline" ? "light-offline" : d.actuatorOn ? "light-on" : "light-off"}`}>
              <i aria-hidden="true" />{d.name}<small>{d.alert === "Offline" ? "offline" : d.actuatorOn ? "on" : "off"}</small>
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

function IngestionView({ active }) {
  const [refreshKey, setRefreshKey] = useState(0);
  const refresh = () => setRefreshKey((k) => k + 1);

  return (
    <div>
      <div className="page-head">
        <div>
          <p className="eyebrow">Tab 1 &middot; Part 1</p>
          <h2>Sensor Data Ingestion &amp; Telemetry</h2>
        </div>
      </div>
      <LiveGauges active={active} />
      <div className="grid">
        <RegisterSensorForm onRegistered={refresh} />
        <DeviceImportCard onImported={refresh} />
        <AnomalyFeed active={active} />
        <LiveTelemetryPanel active={active} />
        <RecursionDemo />
        <OperatorOverloadDemo />
      </div>
      <SensorList refreshKey={refreshKey} active={active} />
    </div>
  );
}
