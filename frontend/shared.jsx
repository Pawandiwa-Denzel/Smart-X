// Shared helpers used by every view. Loaded first by index.html.
const { useState, useEffect, useCallback, useRef, useMemo, useContext, createContext } = React;

const API_BASE = window.SMARTX_API_BASE || "http://localhost:5080";

// ---------------------------------------------------------------------------
// API helper - never throws. A failed call returns { ok:false } and flips the
// global "API offline" banner instead of crashing whichever view called it.
// ---------------------------------------------------------------------------
const apiStatusListeners = new Set();
let apiOnline = true;

function setApiOnline(value) {
  if (apiOnline === value) return;
  apiOnline = value;
  apiStatusListeners.forEach((fn) => fn(value));
}

// Firebase ID token for the signed-in user (auth.jsx sets this up). The SDK
// caches the token and refreshes it automatically before it expires.
async function authToken() {
  try {
    const user = window.firebase && firebase.apps.length ? firebase.auth().currentUser : null;
    return user ? await user.getIdToken() : null;
  } catch { return null; }
}

async function api(path, options = {}) {
  try {
    const token = await authToken();
    const headers = { ...(options.headers || {}) };
    if (options.body && !(options.body instanceof FormData)) headers["Content-Type"] = "application/json";
    if (token) headers.Authorization = `Bearer ${token}`;

    const res = await fetch(`${API_BASE}${path}`, { ...options, headers });
    setApiOnline(true);
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = text; }
    return { ok: res.ok, status: res.status, data };
  } catch (err) {
    setApiOnline(false);
    return { ok: false, status: 0, data: null, error: err };
  }
}

function useApiOnline() {
  const [online, setOnline] = useState(apiOnline);
  useEffect(() => {
    apiStatusListeners.add(setOnline);
    return () => apiStatusListeners.delete(setOnline);
  }, []);
  return online;
}

function useInterval(callback, delayMs, enabled = true) {
  const saved = useRef(callback);
  useEffect(() => { saved.current = callback; }, [callback]);
  useEffect(() => {
    if (!enabled || delayMs == null) return undefined;
    const id = setInterval(() => saved.current(), delayMs);
    return () => clearInterval(id);
  }, [delayMs, enabled]);
}

// Poll an endpoint while `enabled`; keeps the last good value if a call fails.
function usePoll(path, delayMs, enabled = true) {
  const [data, setData] = useState(null);
  const load = useCallback(async () => {
    if (!path) return;
    const r = await api(path);
    if (r.ok) setData(r.data);
  }, [path]);
  useEffect(() => { if (enabled) load(); }, [load, enabled]);
  useInterval(load, delayMs, enabled);
  return [data, load];
}

// sessionStorage can throw in private windows - treat it as a nice-to-have.
function useSessionState(key, initial) {
  const [value, setValue] = useState(() => {
    try {
      const raw = sessionStorage.getItem(key);
      return raw != null ? JSON.parse(raw) : initial;
    } catch { return initial; }
  });
  useEffect(() => {
    try { sessionStorage.setItem(key, JSON.stringify(value)); } catch { /* ignore */ }
  }, [key, value]);
  return [value, setValue];
}

// ---------------------------------------------------------------------------
// Live event stream (SSE). Opened once by the App shell and shared through
// context, so the feed keeps running while the user switches pages.
// ---------------------------------------------------------------------------
const LiveContext = createContext({ connected: false, packets: [], eventCount: 0, subscribe: () => () => {} });

function useLiveStream() {
  const [connected, setConnected] = useState(false);
  const [packets, setPackets] = useState([]);
  const [eventCount, setEventCount] = useState(0);
  const listeners = useRef(new Set());

  useEffect(() => {
    let source;
    let retry;
    let pending = [];
    let flushTimer;

    // Packets can arrive at 50+/s during a burst. Batch them into one React
    // update every 250ms so the UI stays smooth.
    const flush = () => {
      flushTimer = null;
      if (pending.length === 0) return;
      const batch = pending;
      pending = [];
      setPackets((prev) => [...batch.reverse(), ...prev].slice(0, 40));
      setEventCount((c) => c + batch.length);
    };

    let stopped = false;
    // EventSource can't send headers, so the Firebase token goes in the URL.
    // A fresh token is fetched on every (re)connect.
    const connect = async () => {
      const token = await authToken();
      if (stopped) return;
      source = new EventSource(`${API_BASE}/api/events?access_token=${encodeURIComponent(token || "")}`);
      source.addEventListener("hello", () => setConnected(true));
      source.addEventListener("packet", (e) => {
        pending.push(JSON.parse(e.data));
        if (!flushTimer) flushTimer = setTimeout(flush, 250);
      });
      ["alert", "command", "automation"].forEach((type) =>
        source.addEventListener(type, (e) => {
          const payload = JSON.parse(e.data);
          listeners.current.forEach((fn) => fn(type, payload));
        }));
      source.onerror = () => {
        setConnected(false);
        source.close();
        retry = setTimeout(connect, 3000);
      };
    };

    connect();
    return () => {
      stopped = true;
      clearTimeout(retry);
      clearTimeout(flushTimer);
      if (source) source.close();
    };
  }, []);

  const subscribe = useCallback((fn) => {
    listeners.current.add(fn);
    return () => listeners.current.delete(fn);
  }, []);

  return { connected, packets, eventCount, subscribe };
}

// ---------------------------------------------------------------------------
// Toasts
// ---------------------------------------------------------------------------
const ToastContext = createContext(() => {});

function ToastHost({ children }) {
  const [toasts, setToasts] = useState([]);
  const push = useCallback((message, tone = "info") => {
    const id = Math.random().toString(36).slice(2);
    setToasts((t) => [...t.slice(-3), { id, message, tone }]);
    setTimeout(() => setToasts((t) => t.filter((x) => x.id !== id)), 4500);
  }, []);
  return (
    <ToastContext.Provider value={push}>
      {children}
      <div className="toast-stack" role="status" aria-live="polite">
        {toasts.map((t) => <div key={t.id} className={`toast toast-${t.tone}`}>{t.message}</div>)}
      </div>
    </ToastContext.Provider>
  );
}

const useToast = () => useContext(ToastContext);

// ---------------------------------------------------------------------------
// Small display helpers
// ---------------------------------------------------------------------------
const UNITS = { moisture: "%", wattage: "W", temperature: "°C" };
const BANDS = { moisture: [20, 80], wattage: [0, 2200], temperature: [-10, 45] };

const CATEGORY_LABEL = {
  Environmental: "Environmental",
  PowerConsumption: "Power",
  Actuator: "Actuator",
};

function categoryName(c) {
  if (typeof c === "number") return ["Environmental", "PowerConsumption", "Actuator"][c];
  return c;
}

function timeAgo(seconds) {
  if (seconds == null) return "never";
  if (seconds < 1) return "just now";
  if (seconds < 60) return `${Math.round(seconds)}s ago`;
  return `${Math.floor(seconds / 60)}m ${Math.round(seconds % 60)}s ago`;
}

function clockTime(iso) {
  return new Date(iso).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

function Sparkline({ values, metric, width = 120, height = 30 }) {
  if (!values || values.length < 2) return <svg width={width} height={height} className="sparkline" />;
  const band = BANDS[metric];
  const lo = Math.min(...values, band ? band[0] : Infinity);
  const hi = Math.max(...values, band ? band[1] : -Infinity);
  const span = hi - lo || 1;
  const x = (i) => (i / (values.length - 1)) * width;
  const y = (v) => height - 2 - ((v - lo) / span) * (height - 4);
  const d = values.map((v, i) => `${i ? "L" : "M"}${x(i).toFixed(1)},${y(v).toFixed(1)}`).join(" ");
  const last = values[values.length - 1];
  const out = band && (last < band[0] || last > band[1]);
  return (
    <svg width={width} height={height} className="sparkline" aria-hidden="true">
      {band && <rect x="0" y={y(band[1])} width={width} height={Math.max(0, y(band[0]) - y(band[1]))} className="spark-band" />}
      <path d={d} className={out ? "spark-line spark-out" : "spark-line"} />
      <circle cx={x(values.length - 1)} cy={y(last)} r="2.5" className={out ? "spark-dot spark-out" : "spark-dot"} />
    </svg>
  );
}
