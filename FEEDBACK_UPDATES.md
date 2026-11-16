# Updates made from lecturer feedback

This file lists every change made in response to lecturer feedback, so the marker can check each point
against the code.

## Final PoE

| # | Requirement / feedback | What changed | Where |
|---|---|---|---|
| F1 | All three menu options must be active in the final submission | Tab 3 **Network Topology & Mesh Routing** is enabled in the navigation and on the landing page ("Coming soon" removed). It stays mounted like the other tabs, so switching keeps its selections | `frontend/app.jsx`, `frontend/landing.jsx`, `frontend/topology.jsx` |
| F2 | Visualise the mesh, trace routing paths by MAC, show an organised historical log for a selected sensor | SVG mesh map (gateway, routers, sensors, actuators; MST vs backup links; status colours), route tracer by MAC or name with per-hop energy/RSSI, and a paged, chartable, CSV-exportable historical log with time-window presets (e.g. 14:00–16:00) | `frontend/topology.jsx` |
| F3 | Use a BST/AVL/Red-Black tree for timestamped history and range queries | Custom **AVL tree** per sensor with rotations, a pruned O(log n + k) range query and O(log n) counting via subtree sizes. Fed by the gateway pipeline. A benchmark against a `List<T>` scan is built into the page | `Services/Topology/AvlTelemetryTree.cs`, `TelemetryHistoryIndex.cs` |
| F4 | Represent the mesh as a Graph (vertices = ESP32s, edges = wireless links) | Weighted, undirected **adjacency-list graph**, built from the live device registry. Edge weights are transmit energy derived from distance | `Services/Topology/MeshGraph.cs`, `TopologyService.cs` |
| F5 | BFS/DFS to find connected devices on a subnet/gateway, which are down, and where to hop | **BFS** subnet explorer: radios reached per hop, radios cut off (down / isolated / re-routed) and the new next hop of every device that used to go through a down radio. The simulator also uses BFS, so isolated devices stop sending | `Services/Topology/MeshRouting.cs`, `TopologyService.Explore`, `DeviceFleetSimulator.cs` |
| F6 | Prim's or Kruskal's MST for the most power-efficient routing | **Prim's MST** from the gateway using a custom **MinHeap**. Routes follow the tree. Recomputed without failed radios (self-healing). Checked against an independent Kruskal implementation in the tests | `Services/Topology/MeshRouting.cs`, `MinHeap.cs`, `backend/SmartX.Tests` |
| F7 | Data structures kept in the dedicated class (Part 2 note #6 carried forward) | `TelemetryHistory`, `MeshTopology` and `FailedMeshNodes` added to `SmartXDataStructures` and shown at `/api/structures` | `Services/SmartXDataStructures.cs` |
| F8 | README must explain how to compile, run and use the app, with in-depth explanations and code examples for Trees, Graphs and MST | README §3 (trees, graphs, BFS, MST with code and complexity), §6.7–6.10 (step-by-step tests), §7 (new endpoints) | `README.md` |
| F9 | Docker files | API multi-stage `Dockerfile`, nginx `frontend/Dockerfile`, `docker-compose.yml`, `.dockerignore` | repo root, `frontend/` |
| F10 | Project completion report | Challenges per task (especially Task 3), key learnings and justified .NET technology recommendations | `docs/` |

## Part 2 (carried forward)

Changes made to the Part 1 work and the Part 2 build in response to the lecturer's Part 2 guidance
notes. They are all still in place.

| # | Feedback | What changed | Where |
|---|---|---|---|
| 1 | Landing page must look like a real Smart-X product, not a student template | Public landing page with a Smart-X logo, an animated mesh-network graphic, a product preview image, a looping screen recording of the live console, a "how it works" section, modules and use cases | `frontend/landing.jsx`, `frontend/assets/` |
| 2 | Users must be authenticated through a real auth API (e.g. Firebase), no hardcoded logins | Firebase Authentication (email/password, Google, password reset) on the frontend. The **API verifies every request's Firebase ID token** (RS256 signature + `aud`/`iss`/`exp` claims) and returns 401 otherwise. Simulated devices use a separate device key | `frontend/auth.jsx`, `backend/SmartX.Api/Services/FirebaseAuth.cs` |
| 3 | After login, show all three modules. Tab switching must not lose data, freeze or crash | Three tabs after sign-in (Tab 3 was greyed out for Part 2 and is now active). Tabs stay mounted, so switching keeps filters, inputs and the selected device. Hidden tabs pause polling. A per-tab error boundary and a never-throwing API helper stop crashes | `frontend/app.jsx`, `frontend/shared.jsx` |
| 4 | File upload must do more than show a success message (e.g. a .txt list of IoT devices) | **Import device list** reads a `.txt` file, validates each line, registers the devices, adds them to the grid and category index, and the simulator starts sending their telemetry automatically. Imported devices now also join the mesh map on Tab 3 | `Services/DeviceListImporter.cs`, `POST /api/devices/import`, `frontend/ingestion.jsx`, `frontend/assets/sample-devices.txt` |
| 5 | Add a visual representation of live telemetry. Simulated data must hit the API like a device | Live radial **gauges** with operating bands, sparklines and actuator **status lights** on Tab 1. The simulator posts every reading over HTTP to `/api/telemetry` with a device key and polls its device shadow for commands | `frontend/ingestion.jsx`, `Services/DeviceFleetSimulator.cs` |
| 6 | Put the data structures in one dedicated class, named by purpose | `SmartXDataStructures` holds every structure, named by purpose. All services use these instances. Live sizes are shown on Tab 2 and at `/api/structures` | `Services/SmartXDataStructures.cs` |
| 7 | Duplicate warnings on the dashboard cost marks | Part 1 anomaly annotations are de-duplicated with a HashSet (one per device/metric until it's back in band). On Tab 2, a sensor flapping on the band edge is grouped as "×N" instead of being listed repeatedly | `Services/TelemetryStore.cs`, `Services/AlertTracker.cs` |
| 8 | README must explain how to test the queue and dictionary implementations, including simulating a high-priority alert | README §6 gives step-by-step dashboard and curl instructions, including the auth headers | `README.md` |
