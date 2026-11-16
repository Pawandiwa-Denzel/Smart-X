// Public landing page (shown before sign-in) + shared brand graphics.

function Logo({ size = 30, showText = true }) {
  return (
    <span className="logo" aria-label="Smart-X">
      <svg width={size} height={size} viewBox="0 0 40 40" aria-hidden="true">
        <defs>
          <linearGradient id="lg-x" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#4f8cff" />
            <stop offset="1" stopColor="#2ec5ff" />
          </linearGradient>
        </defs>
        <path d="M20 2 L36 11 L36 29 L20 38 L4 29 L4 11 Z" fill="url(#lg-x)" />
        <g stroke="#fff" strokeWidth="2.2" strokeLinecap="round">
          <line x1="13" y1="13" x2="27" y2="27" />
          <line x1="27" y1="13" x2="13" y2="27" />
        </g>
        <g fill="#fff">
          <circle cx="13" cy="13" r="2.6" /><circle cx="27" cy="13" r="2.6" />
          <circle cx="13" cy="27" r="2.6" /><circle cx="27" cy="27" r="2.6" />
          <circle cx="20" cy="20" r="3.2" />
        </g>
      </svg>
      {showText && <span className="logo-text">Smart<span>-X</span></span>}
    </span>
  );
}

// Animated mesh: sensor nodes hopping packets to a central gateway. One node
// pulses red to hint at the alerting side of the product.
function MeshIllustration() {
  const nodes = [
    { id: "gw", x: 260, y: 200, r: 22, gw: true },
    { id: "a", x: 110, y: 90 }, { id: "b", x: 250, y: 60 }, { id: "c", x: 400, y: 95 },
    { id: "d", x: 450, y: 220 }, { id: "e", x: 390, y: 330 }, { id: "f", x: 240, y: 345 },
    { id: "g", x: 105, y: 300 }, { id: "h", x: 70, y: 190 }, { id: "i", x: 170, y: 175, alert: true },
    { id: "j", x: 345, y: 200 },
  ];
  const at = Object.fromEntries(nodes.map((n) => [n.id, n]));
  const links = [
    ["a", "b"], ["b", "c"], ["c", "d"], ["d", "e"], ["e", "f"], ["f", "g"], ["g", "h"], ["h", "a"],
    ["a", "i"], ["h", "i"], ["i", "gw"], ["b", "gw"], ["c", "j"], ["d", "j"], ["j", "gw"], ["e", "gw"], ["f", "gw"], ["g", "i"],
  ];
  return (
    <svg viewBox="0 0 520 400" className="mesh-art" role="img" aria-label="Mesh network of sensor nodes routing to a gateway">
      <defs>
        <radialGradient id="mesh-glow" cx="50%" cy="50%" r="50%">
          <stop offset="0" stopColor="#4f8cff" stopOpacity="0.35" />
          <stop offset="1" stopColor="#4f8cff" stopOpacity="0" />
        </radialGradient>
      </defs>
      <circle cx="260" cy="200" r="150" fill="url(#mesh-glow)" />
      {links.map(([a, b], i) => (
        <g key={i}>
          <line x1={at[a].x} y1={at[a].y} x2={at[b].x} y2={at[b].y} className="mesh-link" />
          <line x1={at[a].x} y1={at[a].y} x2={at[b].x} y2={at[b].y} className="mesh-packet"
                style={{ animationDelay: `${(i * 0.37) % 3}s`, animationDuration: `${2.2 + (i % 4) * 0.5}s` }} />
        </g>
      ))}
      {nodes.map((n) => (
        <g key={n.id} transform={`translate(${n.x},${n.y})`}>
          {n.gw ? (
            <>
              <circle r="34" className="gw-ring" />
              <circle r={n.r} className="gw-core" />
              <path d="M-8 4 a10 10 0 0 1 16 0 M-12 -1 a16 16 0 0 1 24 0 M-4 9 a4 4 0 0 1 8 0" className="gw-wifi" />
            </>
          ) : (
            <>
              {n.alert && <circle r="20" className="node-alert-ring" />}
              <circle r="11" className={n.alert ? "node node-alert" : "node"} />
              <rect x="-4" y="-4" width="8" height="8" rx="1.5" className="node-chip" />
            </>
          )}
        </g>
      ))}
      <text x="260" y="252" textAnchor="middle" className="mesh-label">GATEWAY</text>
      <text x="170" y="150" textAnchor="middle" className="mesh-label alert">moisture 14%</text>
    </svg>
  );
}

function FeatureIcon({ kind }) {
  const paths = {
    ingest: <><path d="M4 17h16M7 13l5 4 5-4M12 3v14" /></>,
    stream: <><path d="M3 12h4l3-7 4 14 3-7h4" /></>,
    mesh: <><circle cx="5" cy="6" r="2" /><circle cx="19" cy="6" r="2" /><circle cx="12" cy="18" r="2" /><path d="M7 6h10M6 8l5 8M18 8l-5 8" /></>,
    farm: <><path d="M12 21V11M12 11c0-4 3-7 7-7 0 4-3 7-7 7zM12 14c0-3-2-5-6-5 0 3 2 5 6 5z" /></>,
    home: <><path d="M3 11l9-7 9 7v9H3z M9 20v-6h6v6" /></>,
    grid: <><path d="M13 2L4 14h7l-1 8 9-12h-7z" /></>,
  };
  return (
    <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      {paths[kind]}
    </svg>
  );
}

function LandingPage({ onSignIn }) {
  const [health, setHealth] = useState(null);
  useEffect(() => { api("/api/health").then((r) => setHealth(r.ok ? r.data : null)); }, []);

  return (
    <div className="landing">
      <header className="landing-nav">
        <Logo />
        <nav>
          <a href="#platform">Platform</a>
          <a href="#how">How it works</a>
          <a href="#modules">Modules</a>
        </nav>
        <button onClick={onSignIn}>Sign in</button>
      </header>

      <section className="hero">
        <div className="hero-copy">
          <p className="eyebrow">IoT mesh operations platform &middot; South Africa</p>
          <h1>Every sensor. Every node.<br /><span>One live console.</span></h1>
          <p className="hero-sub">
            Smart-X monitors hydroponic farms, utility trackers and smart-grid installations across fleets of ESP32 nodes,
            with priority alerting, one-click overrides and suggested actions before a fault turns into downtime.
          </p>
          <div className="hero-cta">
            <button onClick={onSignIn}>Sign in to the console</button>
            <a className="btn-ghost" href="#how">See how it works</a>
          </div>
          <div className={`gateway-status ${health ? "up" : "down"}`}>
            <span className="pulse" /> {health ? `Gateway online · ${new Date(health.time).toLocaleTimeString()}` : "Gateway offline - start the API to sign in"}
          </div>
        </div>
        <div className="hero-art">
          <MeshIllustration />
        </div>
      </section>

      <section className="stat-strip" id="platform">
        <div><strong>&lt;100&nbsp;ms</strong><span>critical-alert path through the priority lane</span></div>
        <div><strong>O(1)</strong><span>device lookup per packet by MAC address</span></div>
        <div><strong>12&nbsp;s</strong><span>heartbeat timeout before a node is flagged offline</span></div>
        <div><strong>1-click</strong><span>overrides with full undo history</span></div>
      </section>

      <section className="preview">
        <div className="section-head">
          <p className="eyebrow">Real-time command stream</p>
          <h2>See the whole fleet, act in a click</h2>
          <p className="muted">Live tiles for every device, a priority lane for critical alerts, and suggested actions learned from how your operators respond.</p>
        </div>
        <div className="preview-frame">
          <div className="frame-bar"><span /><span /><span /><em>smartx.local / command-stream</em></div>
          <video src="assets/command-stream.webm" poster="assets/dashboard-preview.jpg" autoPlay muted loop playsInline
                 aria-label="Recording of the Smart-X command stream dashboard">
            <img src="assets/dashboard-preview.jpg" alt="Smart-X command stream dashboard" />
          </video>
        </div>
      </section>

      <section className="how" id="how">
        <div className="section-head">
          <p className="eyebrow">How it works</p>
          <h2>From the ESP32 to your screen</h2>
        </div>
        <ol className="how-steps">
          <li><span className="step-n">1</span><h3>Devices publish</h3><p>Each node posts typed telemetry (float, int or bool) to the gateway API with its device key.</p></li>
          <li><span className="step-n">2</span><h3>Gateway triages</h3><p>Normal readings queue first-in-first-out. Out-of-band readings jump the queue on the priority lane.</p></li>
          <li><span className="step-n">3</span><h3>Engine learns</h3><p>Alerts, searches and commands are analysed to predict breaches and suggest the next action.</p></li>
          <li><span className="step-n">4</span><h3>Operators act</h3><p>Override pumps, relays and valves from the console, and undo any command instantly.</p></li>
        </ol>
      </section>

      <section className="modules" id="modules">
        <div className="section-head">
          <p className="eyebrow">Modules</p>
          <h2>Three pillars, one gateway</h2>
        </div>
        <div className="module-grid">
          <article><FeatureIcon kind="ingest" /><h3>Sensor Data Ingestion &amp; Telemetry</h3><p>Register devices, bulk-import device lists, attach configs and logs, and watch live gauges.</p></article>
          <article><FeatureIcon kind="stream" /><h3>Real-Time Command Stream &amp; History</h3><p>Priority alerting, manual overrides with undo, device search and predictive suggested actions.</p></article>
          <article><FeatureIcon kind="mesh" /><h3>Network Topology &amp; Mesh Routing</h3><p>Graph the mesh, trace routes by MAC, see which radios are cut off, compute the most power-efficient backbone and query sensor history by time range.</p></article>
        </div>
        <div className="use-cases">
          <div><FeatureIcon kind="farm" /><span>Hydroponic farms</span></div>
          <div><FeatureIcon kind="home" /><span>Real-estate utility tracking</span></div>
          <div><FeatureIcon kind="grid" /><span>Smart-grid installations</span></div>
        </div>
      </section>

      <section className="cta-band">
        <h2>Ready to take the console?</h2>
        <button onClick={onSignIn}>Sign in or create an account</button>
      </section>

      <footer className="landing-foot">
        <Logo size={22} />
        <span>&copy; 2026 Smart-X IoT Mesh Ecosystem &middot; PROG7312 POE by Denzel Pawandiwa (ST10457186)</span>
      </footer>
    </div>
  );
}
