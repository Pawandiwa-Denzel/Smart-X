// Authentication with Firebase Authentication (Email/Password + Google).
//
// The browser signs in with the Firebase SDK. Every API call then sends the
// user's Firebase ID token (see api() in shared.jsx) and the .NET API verifies
// that token's signature and claims before doing anything (Services/FirebaseAuth.cs).
// There are no usernames or passwords anywhere in this code.

const FIREBASE_CONFIG = window.SMARTX_FIREBASE_CONFIG || {};
const firebaseReady = !!(window.firebase && FIREBASE_CONFIG.apiKey && FIREBASE_CONFIG.projectId);

if (firebaseReady && !firebase.apps.length) {
  firebase.initializeApp(FIREBASE_CONFIG);
}

const AuthContext = createContext({ user: null, loading: true });
const useAuth = () => useContext(AuthContext);

function AuthProvider({ children }) {
  const [user, setUser] = useState(null);
  const [loading, setLoading] = useState(firebaseReady);

  useEffect(() => {
    if (!firebaseReady) return undefined;
    return firebase.auth().onAuthStateChanged((u) => {
      setUser(u);
      setLoading(false);
    });
  }, []);

  const value = useMemo(() => ({
    user,
    loading,
    ready: firebaseReady,
    signIn: (email, password) => firebase.auth().signInWithEmailAndPassword(email, password),
    register: async (name, email, password) => {
      const cred = await firebase.auth().createUserWithEmailAndPassword(email, password);
      if (name) await cred.user.updateProfile({ displayName: name });
      return cred;
    },
    signInWithGoogle: () => firebase.auth().signInWithPopup(new firebase.auth.GoogleAuthProvider()),
    resetPassword: (email) => firebase.auth().sendPasswordResetEmail(email),
    signOut: () => firebase.auth().signOut(),
  }), [user, loading]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

// Firebase error codes -> something an operator can act on.
function friendlyAuthError(err) {
  const code = err && err.code ? err.code : "";
  const map = {
    "auth/invalid-email": "That email address doesn't look right.",
    "auth/missing-password": "Enter your password.",
    "auth/invalid-credential": "Email or password is incorrect.",
    "auth/wrong-password": "Email or password is incorrect.",
    "auth/user-not-found": "No account with that email - create one instead.",
    "auth/email-already-in-use": "An account with that email already exists - sign in instead.",
    "auth/weak-password": "Use at least 6 characters for the password.",
    "auth/too-many-requests": "Too many attempts. Wait a minute and try again.",
    "auth/popup-closed-by-user": "Google sign-in was closed before it finished.",
    "auth/network-request-failed": "Can't reach Firebase - check your internet connection.",
    "auth/operation-not-allowed": "This sign-in method isn't enabled in the Firebase console (Authentication > Sign-in method).",
    "auth/unauthorized-domain": "Add this domain (e.g. localhost) under Authentication > Settings > Authorized domains.",
  };
  return map[code] || (err && err.message) || "Sign-in failed.";
}

function LoginPage({ onBack }) {
  const auth = useAuth();
  const [mode, setMode] = useState("signin"); // signin | register | reset
  const [name, setName] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(null);
  const [notice, setNotice] = useState(null);
  const [apiStatus, setApiStatus] = useState(null);

  // Public endpoint - tells us if the API side of Firebase is set up.
  useEffect(() => {
    api("/api/auth/status").then((r) => setApiStatus(r.ok ? r.data : { configured: false, unreachable: true }));
  }, []);

  async function submit(e) {
    e.preventDefault();
    setError(null);
    setNotice(null);
    setBusy(true);
    try {
      if (mode === "signin") await auth.signIn(email.trim(), password);
      else if (mode === "register") await auth.register(name.trim(), email.trim(), password);
      else {
        await auth.resetPassword(email.trim());
        setNotice("Password reset email sent - check your inbox.");
        setMode("signin");
      }
    } catch (err) {
      setError(friendlyAuthError(err));
    } finally {
      setBusy(false);
    }
  }

  async function google() {
    setError(null);
    try { await auth.signInWithGoogle(); } catch (err) { setError(friendlyAuthError(err)); }
  }

  return (
    <div className="auth-screen">
      <div className="auth-art" aria-hidden="true">
        <MeshIllustration />
        <div className="auth-art-copy">
          <h2>Operate every node from one console.</h2>
          <p>Live telemetry, priority alerting and predictive actions for your Smart-X mesh.</p>
        </div>
      </div>

      <div className="auth-panel">
        <button className="link back" onClick={onBack}>&larr; Back to home</button>
        <Logo />
        <h1>{mode === "register" ? "Create your operator account" : mode === "reset" ? "Reset your password" : "Sign in to the gateway"}</h1>
        <p className="muted">Secured with Firebase Authentication. Every API request is verified on the server.</p>

        {!auth.ready && <FirebaseSetupNotice />}
        {auth.ready && apiStatus && !apiStatus.configured && (
          <div className="auth-warn">
            {apiStatus.unreachable
              ? <>The gateway API isn't reachable at <code>{API_BASE}</code>. Start it before signing in.</>
              : <>The API doesn't have a Firebase project ID yet. Set <code>Firebase:ProjectId</code> in <code>backend/SmartX.Api/appsettings.json</code> to <code>{FIREBASE_CONFIG.projectId}</code>.</>}
          </div>
        )}

        {auth.ready && (
          <form onSubmit={submit} className="auth-form">
            {mode === "register" && (
              <>
                <label htmlFor="name">Full name</label>
                <input id="name" value={name} onChange={(e) => setName(e.target.value)} autoComplete="name" placeholder="Denzel Pawandiwa" />
              </>
            )}
            <label htmlFor="email">Work email</label>
            <input id="email" type="email" required value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="email" placeholder="you@company.co.za" />
            {mode !== "reset" && (
              <>
                <label htmlFor="password">Password</label>
                <input id="password" type="password" required minLength={6} value={password} onChange={(e) => setPassword(e.target.value)}
                       autoComplete={mode === "register" ? "new-password" : "current-password"} />
              </>
            )}
            {error && <div className="auth-error" role="alert">{error}</div>}
            {notice && <div className="auth-notice">{notice}</div>}
            <button type="submit" disabled={busy} className="auth-submit">
              {busy ? "Please wait..." : mode === "register" ? "Create account" : mode === "reset" ? "Send reset link" : "Sign in"}
            </button>
            {mode !== "reset" && (
              <>
                <div className="divider"><span>or</span></div>
                <button type="button" className="ghost google" onClick={google}>
                  <svg width="16" height="16" viewBox="0 0 48 48" aria-hidden="true"><path fill="#FFC107" d="M43.6 20.5H42V20H24v8h11.3C33.7 32.7 29.2 36 24 36c-6.6 0-12-5.4-12-12s5.4-12 12-12c3 0 5.8 1.1 7.9 3l5.7-5.7C34 6.1 29.3 4 24 4 12.9 4 4 12.9 4 24s8.9 20 20 20 20-8.9 20-20c0-1.3-.1-2.4-.4-3.5z"/><path fill="#FF3D00" d="M6.3 14.7l6.6 4.8C14.7 15.1 19 12 24 12c3 0 5.8 1.1 7.9 3l5.7-5.7C34 6.1 29.3 4 24 4 16.3 4 9.7 8.3 6.3 14.7z"/><path fill="#4CAF50" d="M24 44c5.2 0 9.9-2 13.4-5.2l-6.2-5.2C29.2 35.1 26.7 36 24 36c-5.2 0-9.6-3.3-11.3-8l-6.5 5C9.5 39.6 16.2 44 24 44z"/><path fill="#1976D2" d="M43.6 20.5H42V20H24v8h11.3c-.8 2.2-2.2 4.2-4.1 5.6l6.2 5.2C37 39.2 44 34 44 24c0-1.3-.1-2.4-.4-3.5z"/></svg>
                  Continue with Google
                </button>
              </>
            )}
          </form>
        )}

        {auth.ready && (
          <div className="auth-switch">
            {mode === "signin" && <>New operator? <button className="link" onClick={() => setMode("register")}>Create an account</button> &middot; <button className="link" onClick={() => setMode("reset")}>Forgot password?</button></>}
            {mode !== "signin" && <>Already have an account? <button className="link" onClick={() => setMode("signin")}>Sign in</button></>}
          </div>
        )}
      </div>
    </div>
  );
}

function FirebaseSetupNotice() {
  return (
    <div className="auth-warn">
      <strong>Firebase isn't configured yet.</strong>
      <ol>
        <li>Firebase console &rarr; Project settings &rarr; Your apps &rarr; Web app &rarr; copy the <code>firebaseConfig</code> values.</li>
        <li>Paste them into <code>frontend/firebase-config.js</code>.</li>
        <li>Set <code>Firebase:ProjectId</code> in <code>backend/SmartX.Api/appsettings.json</code>.</li>
        <li>Enable <em>Email/Password</em> under Authentication &rarr; Sign-in method, then reload.</li>
      </ol>
    </div>
  );
}

function UserMenu() {
  const auth = useAuth();
  const [open, setOpen] = useState(false);
  const u = auth.user;
  if (!u) return null;
  const label = u.displayName || u.email || "Operator";
  const initials = label.split(/[\s@.]+/).filter(Boolean).slice(0, 2).map((p) => p[0].toUpperCase()).join("");
  return (
    <div className="user-menu">
      <button className="avatar" onClick={() => setOpen((o) => !o)} aria-expanded={open} aria-label="Account menu">
        {u.photoURL ? <img src={u.photoURL} alt="" /> : initials}
      </button>
      {open && (
        <div className="user-pop" onMouseLeave={() => setOpen(false)}>
          <strong>{label}</strong>
          <span className="muted small">{u.email}</span>
          <span className="muted small">Signed in with Firebase &middot; {u.providerData[0]?.providerId === "google.com" ? "Google" : "Email"}</span>
          <button className="ghost" onClick={() => auth.signOut()}>Sign out</button>
        </div>
      )}
    </div>
  );
}
