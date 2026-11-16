// Firebase web app config - Firebase console > Project settings > General >
// "Your apps" > Web app > SDK setup and configuration > Config.
//
// These values identify your Firebase project; they are not secrets (Firebase
// web configs are meant to be public). Security comes from the API verifying
// every user's signed ID token.
//
// Also set the SAME projectId in backend/SmartX.Api/appsettings.json -> Firebase:ProjectId
// and enable Email/Password (and optionally Google) under Authentication > Sign-in method.
window.SMARTX_FIREBASE_CONFIG = {
 apiKey: "AIzaSyAMzockOtsISYG9f-uGN3AWUnco1Mx2hFI",
  authDomain: "smartx-gateway-aee9d.firebaseapp.com",
  projectId: "smartx-gateway-aee9d",
  storageBucket: "smartx-gateway-aee9d.firebasestorage.app",
  messagingSenderId: "746707901543",
  appId: "1:746707901543:web:c74bf8640ff1b066ee48c2"
};
