// App shell: public landing page -> Firebase sign-in -> the three-tab console.
//
// Tabs after sign-in:
//   1. Sensor Data Ingestion & Telemetry   (Part 1)   #/ingestion
//   2. Real-Time Command Stream & History  (Part 2)   #/commands
//   3. Network Topology & Mesh Routing     (Final PoE) #/topology
//
// Each tab stays MOUNTED after its first visit and is only hidden when you
// leave it, so switching tabs never loses form input, filters, the selected
// device or scroll position. Hidden tabs pause their polling via `active`.

const TABS = [
  { id: "ingestion", label: "Sensor Data Ingestion & Telemetry", short: "Ingestion & Telemetry", n: 1 },
  { id: "commands", label: "Real-Time Command Stream & History", short: "Command Stream", n: 2 },
  { id: "topology", label: "Network Topology & Mesh Routing", short: "Topology & Mesh", n: 3 },
];

function readRoute() {
  const id = window.location.hash.replace(/^#\/?/, "");
  const tab = TABS.find((t) => t.id === id && !t.disabled);
  return tab ? tab.id : "ingestion";
}

function useHashRoute() {
  const [route, setRoute] = useState(readRoute);
  useEffect(() => {
    const onChange = () => setRoute(readRoute());
    window.addEventListener("hashchange", onChange);
    return () => window.removeEventListener("hashchange", onChange);
  }, []);
  const navigate = useCallback((id) => { window.location.hash = `/${id}`; }, []);
  return [route, navigate];
}

function NavBar({ route, navigate, live, online, alertCount }) {
  return (
    <header className="nav">
      <button className="brand" onClick={() => navigate("ingestion")} aria-label="Smart-X home">
        <Logo size={28} />
        <small>Gateway Console</small>
      </button>
      <nav className="nav-links" aria-label="Modules">
        {TABS.map((t) => (
          <button
            key={t.id}
            className={`nav-link ${route === t.id ? "nav-active" : ""} ${t.disabled ? "nav-disabled" : ""}`}
            onClick={t.disabled ? undefined : () => navigate(t.id)}
            disabled={t.disabled}
            aria-current={route === t.id ? "page" : undefined}
            title={t.disabled ? "Network Topology & Mesh Routing - available in the final PoE" : t.label}
          >
            <span className="tab-n">{t.n}</span>
            {t.short}
            {t.id === "commands" && alertCount > 0 && <span className="nav-badge">{alertCount}</span>}
            {t.disabled && <span className="nav-soon">Final PoE</span>}
          </button>
        ))}
      </nav>
      <div className="nav-status">
        <span className={`status-pill ${online ? "ok" : "bad"}`}>{online ? "API online" : "API offline"}</span>
        <span className={`status-pill ${live.connected ? "ok" : "bad"}`}><span className="pulse" />{live.connected ? "Live" : "Reconnecting"}</span>
        <UserMenu />
      </div>
    </header>
  );
}

function OfflineBanner() {
  return (
    <div className="offline-banner" role="alert">
      Can't reach the gateway API at <code>{API_BASE}</code>. Your work on this page is kept - it will reconnect automatically once the API is back.
    </div>
  );
}

// Only mounted once the user is signed in, so the live stream and polling
// always carry a Firebase token.
function Console() {
  const [route, navigate] = useHashRoute();
  const live = useLiveStream();
  const online = useApiOnline();
  const [alertSnapshot] = usePoll("/api/alerts/active", 4000, true);
  const alertCount = (alertSnapshot?.activeErrors?.length || 0) + (alertSnapshot?.disconnected?.length || 0);

  // Coming from #/login (or any unknown hash) -> show the real tab in the URL.
  useEffect(() => {
    if (!TABS.some((t) => `#/${t.id}` === window.location.hash && !t.disabled)) {
      history.replaceState(null, "", `#/${route}`);
    }
  }, [route]);

  const [visited, setVisited] = useState(() => new Set([route]));
  useEffect(() => {
    setVisited((v) => (v.has(route) ? v : new Set([...v, route])));
    window.scrollTo({ top: 0 });
  }, [route]);

  const views = {
    ingestion: (a) => <IngestionView active={a} />,
    topology: (a) => <TopologyView active={a} />,
    commands: (a) => <CommandStreamView active={a} />,
  };

  return (
    <LiveContext.Provider value={live}>
      <NavBar route={route} navigate={navigate} live={live} online={online} alertCount={alertCount} />
      <main className="app-shell">
        {!online && <OfflineBanner />}
        {TABS.filter((t) => !t.disabled && visited.has(t.id)).map((t) => (
          <div key={t.id} hidden={route !== t.id}>
            <ErrorBoundary name={t.label}>{views[t.id](route === t.id)}</ErrorBoundary>
          </div>
        ))}
      </main>
    </LiveContext.Provider>
  );
}

function App() {
  const auth = useAuth();
  const [showLogin, setShowLogin] = useState(() => window.location.hash === "#/login");

  if (auth.loading) {
    return <div className="splash"><Logo size={44} /><span className="muted">Checking your session...</span></div>;
  }

  if (!auth.user) {
    return showLogin
      ? <LoginPage onBack={() => { setShowLogin(false); window.location.hash = ""; }} />
      : <LandingPage onSignIn={() => { setShowLogin(true); window.location.hash = "/login"; window.scrollTo({ top: 0 }); }} />;
  }

  return <Console />;
}

// If one view throws while rendering, only that view shows an error - the
// nav and the other tab keep working.
class ErrorBoundary extends React.Component {
  constructor(props) { super(props); this.state = { error: null }; }
  static getDerivedStateFromError(error) { return { error }; }
  render() {
    if (this.state.error) {
      return (
        <div className="card">
          <h3>{this.props.name} hit an error</h3>
          <p className="muted">{String(this.state.error.message || this.state.error)}</p>
          <button onClick={() => this.setState({ error: null })}>Try again</button>
        </div>
      );
    }
    return this.props.children;
  }
}

ReactDOM.createRoot(document.getElementById("root")).render(
  <AuthProvider><ToastHost><App /></ToastHost></AuthProvider>
);
