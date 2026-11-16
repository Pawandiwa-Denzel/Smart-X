# Smart-X IoT Mesh Ecosystem

**PROG7312 · Portfolio of Evidence (final) — Network Topology & Mesh Routing**
Denzel Pawandiwa · ST10457186

A .NET 10 Minimal API gateway that ingests telemetry from a simulated fleet of ESP32-class
devices, plus a React console. Visitors land on a product landing page, sign in with
**Firebase Authentication**, and then get three tabs, all active:

| Tab | Status | Route |
|---|---|---|
| 1. Sensor Data Ingestion & Telemetry | Part 1 (live gauges + `.txt` device import added) | `#/ingestion` |
| 2. Real-Time Command Stream & History | Part 2 | `#/commands` |
| 3. Network Topology & Mesh Routing | **Final PoE**: mesh graph, BFS, Prim's MST routing, AVL history | `#/topology` |

---

## Contents


1. [Quick start](#1-quick-start)
2. [Architecture](#2-architecture)
3. [Final PoE: Trees, Graphs and Minimum Spanning Trees](#3-final-poe-trees-graphs-and-minimum-spanning-trees)
4. [Part 2 data structures — what, where and why](#4-part-2-data-structures)
5. [Predictive Action Engine](#5-predictive-action-engine)
6. [How to test every data structure](#6-how-to-test-each-data-structure)
7. [API reference](#7-api-reference)
8. [Configuration](#8-configuration)
9. [Part 1 concepts (still in place)](#9-part-1-concepts-still-in-place)
10. [Troubleshooting](#10-troubleshooting)
11. [Authentication & device import](#11-authentication--device-import)
12. [Project report, Docker and commit history](#12-project-report-docker-and-commit-history)
13. [References](#13-references)


## 1. Quick start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (`dotnet --list-sdks` should show `10.0.x`)
- Python 3 (only used as a tiny static file server for the frontend) **or** Docker Desktop
- A modern browser (Chrome / Edge / Firefox). The frontend loads React from a CDN, so it needs internet access.

### Option A — run locally (two terminals)

**Terminal 1 — backend API**

```bash
cd backend/SmartX.Api
dotnet restore
dotnet build
dotnet run --launch-profile http
```

The API listens on **http://localhost:5080**. Check it with:

```bash
curl http://localhost:5080/api/health
```

On start-up the built-in device simulator provisions 9 devices and begins posting telemetry to
the API over HTTP, so the dashboard has live data immediately.

**Terminal 2 — frontend**

```bash
cd frontend
python -m http.server 5173
```

Open **http://localhost:5173** in your browser. You'll see the landing page. Click **Sign in to the
console**, then **Create an account** (or sign in with Google). After signing in you land on Tab 1.

> The frontend has no build step. React and Babel are loaded from cdnjs and the `.jsx` files are
> compiled in the browser. It must be **served over http** (opening `index.html` directly from disk will
> not load the `.jsx` files).

### Option B — Docker Compose

```bash
docker compose up --build
```

- API → http://localhost:5080 (`Dockerfile`: .NET 10 SDK build stage → ASP.NET runtime image)
- Dashboard → http://localhost:5173 (`frontend/Dockerfile`: nginx serving the static console)

Stop with `Ctrl+C`, then `docker compose down`.

---

## 2. Architecture

```
 ┌──────────────────────┐   POST /api/telemetry (HTTP, like a real board)
 │ DeviceFleetSimulator │ ─────────────────────────────────────────────┐
 │ 9 simulated ESP32s   │ ◄── GET /api/devices/{mac}/shadow ──┐        │
 └──────────────────────┘     (pumps/relays pick up commands) │        ▼
                                                              │  ┌───────────────┐
                                                              │  │GatewayPipeline│ parse once (float/int/bool)
                                                              │  │   .Accept()   │ classify severity by band
                                                              │  └──────┬────────┘
                                                              │         │
                                  ┌───────────────────────────┴──┬──────┴─────────────┐
                                  │  DualLaneQueue<QueuedPacket> │                    │
                                  │  ├ PriorityQueue (critical) ◄┘ out of band        │
                                  │  └ Queue<T> FIFO (normal)   ◄── in band ──────────┘
                                  └──────────────┬───────────────┘
                                                 │ TelemetryProcessor (rate-limited worker,
                                                 ▼ always drains the priority lane first)
   ┌────────────────────┬────────────────────────┼───────────────────────┬────────────────────┐
   ▼                    ▼                        ▼                       ▼                    ▼
 TelemetryStore    DeviceStateStore          AlertTracker           ActionEngine        LiveBroadcaster
 (Part 1 typed     Dictionary<MAC,state>     HashSet active errors   pattern table +     SSE fan-out to
 List<Packet<T>>)  + SortedDictionary        HashSet disconnected    trend prediction    every dashboard
                   history per device        (dedupe)
                                                                            ▲
 CommandService ── Stack<CommandRecord> undo / redo ────────────────────────┘ (commands are learned)
```

**Final PoE additions.** After processing, every numeric reading is also inserted into that sensor's
**AVL tree** (`TelemetryHistoryIndex`). `TopologyService` builds the **mesh graph** from the device
registry and, on each request, runs **Prim's MST** (with a **MinHeap**) from the gateway, leaving out
radios that are failed or silent. **BFS** answers "who is still connected?", and the simulator uses it
too, so a device with no path to the gateway stops sending.

```
 GatewayPipeline ──► TelemetryHistoryIndex ──► Dictionary<MAC, AvlTelemetryTree>  ──► /api/history (range queries)
 DeviceStateStore ─► TopologyService ─► MeshGraph (adjacency list) ─► BFS (Queue) / Prim (MinHeap) ─► /api/topology
                          ▲                                                     │
 DeviceFleetSimulator ────┘ CanReachGateway? (no path → device goes silent)      └─► route, re-route, isolated
```

### Backend layout (`backend/SmartX.Api`)

| File | Role |
|---|---|
| `Program.cs` | DI registrations, Part 1 endpoints, `/api/telemetry` now queues instead of storing directly |
| `Endpoints/CommandStreamEndpoints.cs` | All Part 2 routes |
| `Models/RealtimeModels.cs` | Part 2 records/enums (packets, device state, commands, suggestions, insights) |
| `Services/DualLaneQueue.cs` | Custom generic two-lane queue (`Queue<T>` + `PriorityQueue<T,…>`) |
| `Services/GatewayPipeline.cs` | Accepts, classifies and processes packets; lane statistics |
| `Services/BackgroundWorkers.cs` | `TelemetryProcessor`, `HeartbeatMonitor`, `AutomationLoop` |
| `Services/DeviceStateStore.cs` | Live `Dictionary` by MAC, category index, `SortedDictionary` history |
| `Services/AlertTracker.cs` | `HashSet` active errors / disconnected nodes, duplicate suppression |
| `Services/CommandService.cs` | Manual overrides with `Stack<T>` undo / redo |
| `Services/ActionEngine.cs` | Predictive Action Engine (patterns, suggestions, insights) |
| `Services/LiveBroadcaster.cs` | Server-Sent Events (`TypedResults.ServerSentEvents`, new in .NET 10) |
| `Services/DeviceFleetSimulator.cs` | Simulated device fleet that talks to the API over HTTP |
| `Services/SensorBands.cs` | Operating / critical bands per metric |
| `Services/SmartXDataStructures.cs` | **Every Part 2 data structure, named by purpose** |
| `Services/FirebaseAuth.cs` | Firebase ID-token verification + auth middleware |
| `Services/DeviceListImporter.cs` | Parses an uploaded `.txt` device list and registers the devices |
| `Services/Topology/*.cs` | **Final PoE**: AVL tree, history index, MinHeap, mesh graph, BFS + Prim routing, topology service (§3) |
| `Endpoints/TopologyEndpoints.cs` | Final PoE routes (`/api/topology/*`, `/api/history/*`) |

`backend/SmartX.Tests` is a separate console project with 31 automated checks for the final PoE structures
(§6.10). `backend/SmartX.slnx` opens both projects in Visual Studio / Rider.

### Frontend layout (`frontend/`)

| File | Role |
|---|---|
| `index.html` | Loads React + the `.jsx` files in order |
| `shared.jsx` | `api()` helper (never throws), polling hooks, SSE hook, toasts, sparkline |
| `firebase-config.js` | Your Firebase web config (fill this in, see §0) |
| `auth.jsx` | Firebase sign-in / sign-up / reset / Google, user menu |
| `landing.jsx` | Public landing page, Smart-X logo, animated mesh graphic |
| `app.jsx` | Landing → login → three-tab console, hash router, keep-alive tabs, error boundary |
| `ingestion.jsx` | Tab 1: live gauges, sensor registration, `.txt` device import, attachments, Part 1 demos |
| `commandstream.jsx` | Tab 2: Real-Time Command Stream |
| `topology.jsx` | Tab 3: mesh map, route tracer, subnet explorer (BFS), MST card, AVL historical log |
| `Dockerfile` | nginx image that serves the console (used by Docker Compose) |
| `assets/` | Logo/favicon, dashboard preview image and video, sample device list |

**Navigation without losing state.** All three tabs are active. Routing is hash-based (`#/commands`, `#/topology`), so back/forward and
refresh work. Each view stays **mounted** after its first visit and is only hidden when you leave it,
so form input, search text, filters, selected device and scroll position are all kept. Hidden views
pause their polling (`active` prop). The live SSE stream is opened once by the app shell, so it keeps
running while you move between pages. If the API goes down, a banner appears and every view keeps its
last data. An error boundary per view means one broken page can't take down the others.

---

## 3. Final PoE: Trees, Graphs and Minimum Spanning Trees

Tab 3 (**Network Topology & Mesh Routing**) needed three things the Part 2 collections couldn't do well:
fast time-range queries over a sensor's whole history, a model of which radios can hear each other, and
a way to pick the cheapest set of links that still connects everything. Each one has its own custom
structure in `backend/SmartX.Api/Services/Topology/`, and every instance is declared in
`SmartXDataStructures` next to the Part 2 structures.

| Name in `SmartXDataStructures` | Type | Job |
|---|---|---|
| `TelemetryHistory` | `Dictionary<string, AvlTelemetryTree>` | One self-balancing tree of timestamped readings per sensor (MAC → tree) |
| `MeshTopology` | `MeshGraph` (adjacency list) | Vertices = gateway, routers and ESP32 devices. Edges = wireless links with an energy weight |
| `FailedMeshNodes` | `HashSet<string>` | Radios the operator has taken down (self-healing demo) |
| *(inside Prim's algorithm)* | `MinHeap<T>` | Hands out the cheapest candidate link in O(log E) |
| *(inside BFS)* | `Queue<T>` | Explores the mesh one hop at a time |

| File | What's in it |
|---|---|
| `Services/Topology/AvlTelemetryTree.cs` | AVL tree: insert with rotations, pruned range query, rank-based counting, invariant check |
| `Services/Topology/TelemetryHistoryIndex.cs` | Owns the trees, feeds them from the gateway pipeline, seeds 24 h of history, list-scan benchmark |
| `Services/Topology/MinHeap.cs` | Array-backed binary min-heap |
| `Services/Topology/MeshGraph.cs` | `MeshNode`, `MeshLink`, the adjacency-list graph and radio-range linking |
| `Services/Topology/MeshRouting.cs` | Breadth-First Search, Prim's MST, path reconstruction |
| `Services/Topology/TopologyService.cs` | Builds the mesh from the live device registry, routes, failures, subnet exploration |
| `Endpoints/TopologyEndpoints.cs` | `/api/topology/*` and `/api/history/*` routes |
| `backend/SmartX.Tests/Program.cs` | 31 automated checks against brute-force and Kruskal reference results |

### 3.1 Tree: AVL tree for historical telemetry

**Problem.** An operator asks: *"Show me Moisture Sensor B between 14:00 and 16:00."* The seeded history
alone is 48 h at one reading every 15 s (~11 520 readings per sensor), and live readings keep arriving. A
`List<T>` has to check every reading to answer that. A `SortedDictionary` keeps keys in order, but it
has no "start at 14:00" operation, so `Where(k => k >= from && k <= to)` still walks from the very first
reading.

**Why a balanced tree.** A binary search tree keyed by timestamp lets the query jump straight to 14:00.
But telemetry arrives **already sorted by time**, which is the worst case for a plain BST: every new
key goes to the right, the tree turns into a linked list (height = n), and every query is O(n) again.
An **AVL tree** checks the height difference of each node's two subtrees after every insert and
**rotates** when it reaches 2, so the height stays below about 1.44·log₂(n). With the seeded history
of ~11 520 readings the tree is only **14 levels** deep.

```csharp
// AvlTelemetryTree.Rebalance - called on the way back up after every insert
if (balance > 1)                       // left heavy
{
    if (BalanceOf(node.Left!) < 0)     // left-right case
        node.Left = RotateLeft(node.Left!);
    return RotateRight(node);          // left-left case
}
if (balance < -1)                      // right heavy
{
    if (BalanceOf(node.Right!) > 0)    // right-left case
        node.Right = RotateRight(node.Right!);
    return RotateLeft(node);           // right-right case (sequential timestamps land here)
}
```

**Range query, O(log n + k).** The query only goes left if the window starts before this node, and only
goes right if the window ends after it. Whole subtrees outside 14:00–16:00 are skipped without being
visited. An in-order walk means results come out already sorted by time.

```csharp
// AvlTelemetryTree.Collect - pruned in-order traversal
if (node is null || output.Count >= limit) return;
visited++;
if (from < node.Key) Collect(node.Left, from, to, limit, output, ref visited);   // window starts left of me
if (node.Key >= from && node.Key <= to) output.Add(new HistoryPoint(node.Key, node.Value));
if (node.Key < to)   Collect(node.Right, from, to, limit, output, ref visited);  // window ends right of me
```

**Counting without collecting.** Every node also stores its **subtree size**, so the number of readings
in a window is `rank(≤ to) − rank(< from)`, which is two root-to-leaf walks: O(log n). The page uses this
for "Readings in range" even when the returned points are capped.

**Timestamp collisions.** Two readings can share a timestamp during a burst. `Insert` nudges the new key
forward by one tick so nothing is overwritten (the same rule as the Part 2 `SortedDictionary`).

**Scaling.**

| Operation | `List<T>` scan | AVL tree |
|---|---|---|
| Insert | O(1) | O(log n) |
| Range query returning k readings | O(n) | O(log n + k) |
| Count in a range | O(n) | O(log n) |
| Height after ~11 520 sorted inserts | n/a | 14 (a plain BST would be 11 520) |

The **Compare with list scan** button on Tab 3 runs the same window against a shuffled `List<T>` copy and
shows both timings. For a 2-hour window on an 11 523-reading tree, the AVL query visited 496 nodes
(480 results) and was about **10×** faster than scanning all 11 523 list items (25 µs vs 250 µs).
The gap grows with n: the tree's cost depends on the window, the list's on the whole history.

**Where it's used.** `GatewayPipeline` inserts every numeric reading after processing it
(`history.Add(p.DeviceId, p.Metric, p.ReceivedAt, p.NumericValue)`), and the **Historical telemetry log**
card on Tab 3 queries it.

### 3.2 Graph: the mesh as an adjacency list

**Model.** Each radio is a **vertex** (`MeshNode`: MAC, name, kind, subnet, position). Each wireless link
two radios can both hear is an undirected, weighted **edge** (`MeshLink`). Links are created when two
radios are within range of each other (`LinkByRadioRange`). Each edge carries:

- **distance**,
- **RSSI**: log-distance path loss, `−40 − 20·log₁₀(d / 10)` dBm,
- **PowerCost**: the energy to send one packet across it, `1 + 4·(d / 200)²` mJ. Transmit power grows with
  the square of distance, so one long hop costs more than two short ones.

```csharp
// MeshGraph - weighted, undirected graph stored as an adjacency list
private readonly Dictionary<string, MeshNode> _nodes;                               // vertices by MAC
private readonly Dictionary<string, Dictionary<string, MeshLink>> _adjacency;       // MAC -> neighbour MAC -> link

public void AddLink(MeshLink link)
{
    _adjacency[link.A][link.B] = link;    // undirected: store it both ways
    _adjacency[link.B][link.A] = link;
}
```

**Why an adjacency list.** A mesh is **sparse**: each radio only hears a handful of neighbours. An
adjacency matrix would use O(V²) memory, mostly empty. The list uses O(V + E), and "who can this radio
reach?" is one dictionary lookup. That question is asked constantly by BFS and Prim.

**The mesh.** `TopologyService` builds the graph from the live `DeviceRegistry`: the central gateway, four
mesh routers (Zone 1, Zone 2, Main Board, Facility B) and every device, grouped into subnets by its
location. Devices imported from a `.txt` file on Tab 1 appear on the map automatically. With the default
fleet the graph has **14 vertices and 30 edges**.

### 3.3 Breadth-First Search: who is connected, who is cut off

BFS explores from a start radio **one hop at a time** using a `Queue<T>`. Because it finishes every
radio at distance 1 before any at distance 2, the first time it reaches a radio is by the **fewest hops**.
Radios that are down are never entered, so whatever BFS doesn't reach has lost its connection.

```csharp
// MeshRouting.BreadthFirst
var queue = new Queue<(string Mac, int Hop)>();
queue.Enqueue((start, 0));
parent[start] = null;
while (queue.TryDequeue(out var current))
{
    order.Add(new BfsVisit(current.Mac, current.Hop, parent[current.Mac]));
    foreach (var link in graph.Neighbours(current.Mac).OrderBy(l => l.Distance))
    {
        var next = link.Other(current.Mac);
        if (parent.ContainsKey(next)) continue;                          // already visited
        if (down.Contains(next)) { downSeen.Add(next); continue; }      // offline - don't go through it
        if (subnet is not null && graph.TryGetNode(next, out var n) && n.Subnet != subnet) continue;
        parent[next] = current.Mac;
        queue.Enqueue((next, current.Hop + 1));
    }
}
```

It is used in three places:

1. **Subnet explorer** (Tab 3): BFS from the gateway or a router, optionally **staying inside that
   subnet**. It lists the radios reached at each hop, then everything in scope it **didn't** reach, marked
   **down** (failed / no heartbeat), **isolated** (no path left) or **re-routed** (reachable another way,
   e.g. through a neighbouring subnet). For each down router it lists the devices that used to route
   through it and the next hop they use now.
2. **Fewest-hop path** in the route tracer, to compare with the power-efficient MST route.
3. **Simulator**: before a simulated device sends a packet it checks `CanReachGateway` (a BFS that skips
   failed radios). If there's no path, the device goes silent, the heartbeat monitor notices, and the
   radio shows as isolated. The failure spreads through the system the same way it would in a real mesh.

Cost: O(V + E). Each radio is queued at most once and each link is checked at most twice.

### 3.4 Minimum Spanning Tree: Prim's algorithm with a MinHeap

**Problem.** Every sensor has to get its packets to the gateway, and batteries are limited. Using every
link wastes energy. The cheapest set of links that still connects every radio is the **Minimum Spanning
Tree**: V − 1 edges with the lowest total weight.

**Prim's algorithm** grows the tree from the central gateway. At each step it takes the **cheapest link
that reaches a radio not yet in the tree**. A `MinHeap` keeps the candidate links ordered by power cost,
so the cheapest is always on top. This is the "lazy" version: stale entries (links to radios that have
already joined by a cheaper link) are skipped when popped.

```csharp
// MeshRouting.PrimMst
AddToTree(root);                                   // push every link leaving the gateway
while (heap.TryPop(out var candidate, out var cost))
{
    if (inTree.Contains(candidate.To)) continue;   // stale - that radio already joined more cheaply
    parentLink[candidate.To] = candidate.Link;     // remember how this radio reaches the tree
    edges.Add(candidate.Link);
    total += cost;
    AddToTree(candidate.To);                       // push its links to radios still outside
}
```

```csharp
// MinHeap<T> - array-backed binary heap, parent of i is (i - 1) / 2
public void Push(T item, double priority)
{
    _items.Add((item, priority));
    SiftUp(_items.Count - 1);                      // O(log n)
}
```

**Routing on the tree.** Because the MST is rooted at the gateway, each radio's route home is just its
chain of parents (`PathToRoot`). The route tracer shows each hop's energy and RSSI and the total cost per
packet. For Moisture Sensor B: **4 hops, 11.42 mJ**, while the fewest-hop BFS route is 2 hops but uses
longer, more expensive links.

**Self-healing.** Down radios are left out of the heap, so the MST is recomputed **without** them
every time the page refreshes. Take down the Zone 1 router and Prim reconnects Sensor B through Pump A
and Irrigation Valve 2 (3 hops), and the page marks it **re-routed**. Take down the Main Board router and
the Grid Tie meter has no other neighbour, so it's reported **isolated**. With the default fleet the MST
costs **40.61 mJ** against **143.45 mJ** for every link, about 72% less.

**Prim vs Kruskal.** Kruskal sorts every edge and adds the cheapest one that doesn't create a cycle
(union–find). Both give a minimum spanning tree. Prim was chosen because (1) it **grows from the gateway**,
so the parent links it records are the routes home, with no extra pass; and (2) it only ever looks at
the reachable part of the graph, so radios cut off by a failure fall out naturally as "unreachable".
`backend/SmartX.Tests` implements Kruskal as an independent check and confirms Prim gives the same total
on 30 random meshes.

Cost: O(E log E) with the lazy heap. For this mesh (30 edges) that is a few microseconds, so the tree is
simply recomputed on every snapshot.

### 3.5 Using Tab 3

| Card | What to do |
|---|---|
| **KPI strip** | Radios (vertices) and links (edges), how many are online, down, isolated or re-routed, and the MST's energy saving |
| **Mesh map** | Gateway (Wi-Fi icon), routers (diamonds), sensors (circles), actuators (squares). Blue = MST link, dashed = backup link, green = traced route with animated packet flow. Red X = down, amber = isolated, orange ring = re-routed. Toggle **All radio links / MST only**. Click any radio to trace it |
| **Route tracer** | Type a MAC (`AA:BB:CC:00:02`) or part of a name and press **Trace**. Shows each hop with mJ and dBm, the total, a re-routed badge, the fewest-hop comparison, and a **Simulate failure / Restore** button |
| **Subnet explorer** | Pick the gateway or a router, optionally tick **Stay inside this subnet**, and see BFS level by level plus what was cut off and where its traffic goes now |
| **Power-efficient backbone** | Every link vs MST when healthy vs MST now, edges, heap pushes |
| **Historical telemetry log** | Pick a sensor and a time window (presets: **14:00–16:00**, last hour / 6 h / 24 h). Shows readings in range, min/avg/max, tree size and height, nodes visited and query time, a chart, a paged table (out-of-band rows highlighted), **Compare with list scan** and **CSV** export |

---

## 4. Part 2 data structures

### Stacks, Queues, Priority Queues

| Structure | Where | What it does |
|---|---|---|
| `Queue<QueuedPacket>` | `DualLaneQueue._standard` | FIFO lane for every in-band packet. Readings from one device are processed in the order they arrived. |
| `PriorityQueue<QueuedPacket,(int Severity,long Seq)>` | `DualLaneQueue._priority` | Critical (0) and warning (1) packets. `TryDequeue` **always checks this lane first**, so a power spike skips past a backlog of normal packets. The sequence number in the priority keeps it stable (FIFO among equal severities). |
| `Stack<CommandRecord>` ×2 | `CommandService._undo` / `_redo` | Every manual override is pushed with the before/after state of **all** devices it touched. Undo pops and restores, so "Shut down all valves" is reverted in one step. Redo re-applies. A new command clears the redo stack. |
| `Queue<ActivityEvent>` | `ActionEngine._window` | Sliding 3-minute window of recent alerts/searches/commands for pattern learning. |

```csharp
// DualLaneQueue<T>.TryDequeue — priority lane always wins
if (_priority.TryDequeue(out item!, out _)) { fromPriorityLane = true;  return true; }
if (_standard.TryDequeue(out item!))        { fromPriorityLane = false; return true; }
```

The processor is deliberately **rate-limited** (default 60 packets/s, like a small gateway board with a
fixed CPU budget). That makes the effect visible: during a burst the standard lane backs up for
several seconds while a critical packet is processed on the next 100 ms tick. Both lanes' average and
max wait times are shown on the dashboard, along with a **bypass counter** that counts priority packets
served while normal packets were still waiting.

### Where they live: `Services/SmartXDataStructures.cs`

Every Part 2 structure is declared in **one class**, `SmartXDataStructures`, and named after its job in
the brief. The services get these instances injected and contain the logic that uses them.

| Name in code | Type | Job |
|---|---|---|
| `TelemetryQueue` | `Queue<QueuedPacket>` | FIFO processing of normal telemetry |
| `CriticalAlertQueue` | `PriorityQueue<QueuedPacket,(int,long)>` | Critical alerts bypass the normal queue |
| `CommandUndoStack` / `CommandRedoStack` | `Stack<CommandRecord>` | Undo / redo manual overrides |
| `DeviceRegistry` | `Dictionary<string, DeviceLiveState>` | O(1) device lookup by MAC per packet |
| `CategoryIndex` | `Dictionary<SensorCategory, HashSet<string>>` | Category filter without scanning |
| `SensorTimeline` | `Dictionary<string, SortedDictionary<DateTime,double>>` | Ordered history per device |
| `LastHeartbeat` | `Dictionary<string, DateTime>` | When each device was last heard from |
| `ActiveErrorStates` | `HashSet<string>` | Open alerts, so no duplicates |
| `DisconnectedNodes` | `HashSet<string>` | Offline nodes, so no duplicate disconnect alerts |
| `ActionPatterns` / `TriggerOccurrences` | `Dictionary<…>` | Learned operator behaviour |
| `RecentActivityWindow` | `Queue<ActivityEvent>` | Last 3 minutes of alerts/searches/commands |
| `SearchHistory` / `AutoExecutedActions` | `Dictionary` / `HashSet` | Search counts / actions already automated |

Live sizes are shown in the **Data structures in play** card on Tab 2 and at `GET /api/structures`.
The tables below describe the same structures in more detail. Their *Where* column names the class
holding the logic that uses each one.

### Hash tables, Dictionaries, Sorted Dictionaries

| Structure | Where | What it does |
|---|---|---|
| `Dictionary<string, DeviceLiveState>` | `DeviceStateStore._devices` | Keyed by MAC (case-insensitive). Every processed packet does one O(1) lookup to update its device. |
| `Dictionary<SensorCategory, HashSet<string>>` | `DeviceStateStore._byCategory` | Secondary index. The category filter only looks at the bucket for that category instead of scanning every device. |
| `SortedDictionary<DateTime, double>` | `DeviceLiveState.History` | Per-device timeline. Keys stay in timestamp order, so the API returns timeline data already sorted. Range queries skip to `from` and stop at `to`. Timestamp collisions during a burst are nudged forward one tick so no reading is overwritten. Capped at 600 points (oldest = `Keys.First()`). |
| `Dictionary<string, Dictionary<string,int>>` | `ActionEngine._follows` | Learned "A is followed by B" counts (see §5). |

### Sets

| Structure | Where | What it does |
|---|---|---|
| `HashSet<string>` | `AlertTracker._activeErrors` | Keys like `alert|moisture-low|AA:BB:CC:00:02`. `Add()` returns `false` if the error is already open. That stops a sensor that keeps reporting a bad value from raising the same alert on every packet. |
| `HashSet<string>` | `AlertTracker._disconnected` | MACs of silent nodes. The heartbeat monitor checks every 2 s, but an outage is raised **once** however many times it's noticed. Duplicates are counted on the dashboard ("Duplicates blocked"). |
| `HashSet<string>` | `ActionEngine._autoExecuted` | Makes sure Auto mode runs an action once per alert occurrence, not once every 3 s. |

---

## 5. Predictive Action Engine

`Services/ActionEngine.cs` — shown on the dashboard as **Suggested Actions** and **Automated Insights**.

**1. Pattern analysis.** Every new alert, offline event, search/filter and manual command is recorded.
When the operator does something (search or command), the engine counts it as *following* every
distinct event still in the 3-minute window:

```
_follows["alert|moisture-low|AA:BB:CC:00:02"]["search|AA:BB:CC:00:05"]  = 5   // searched Pump A
_follows["alert|moisture-low|AA:BB:CC:00:02"]["cmd|TurnOn|AA:BB:CC:00:05"] = 5  // turned Pump A on
confidence = count(A → B) / occurrences(A)
```

It's a first-order transition table, a simple Markov / association-rule model. Because alerts are
de-duplicated by the HashSet first, one long outage can't inflate the counts.

**2. Algorithmic suggestion.** The triggers that are live right now (open alerts and offline nodes from the
HashSets, plus the operator's last few actions) are looked up in the table. Candidates are ranked in a
`PriorityQueue<Suggestion, score>` where `score = confidence × (1 − 1/(1+support))`, and live alerts are
weighted above "you just did X". Suggestions that are already satisfied are dropped (for example, "Turn
on Pump A" when it's already on). Command suggestions have a one-click **Run** button.

**3. Prediction before the breach.** For each sensor, a least-squares line is fitted over its last 12
readings from the `SortedDictionary`. If the fit is strong (R² ≥ 0.55), the latest readings agree with the
fitted direction, and the projected time to leave the band is under 10 minutes, the engine raises a
*trend* insight such as *"Moisture Sensor B is falling 11.8%/min. Predicted to cross 20% in ~1m 02s. Last
time this happened you usually: Turn ON Pump A."* The tile is also flagged in the grid **before** anyone
searches for it. Other insights cover offline nodes, "flapping" sensors (≥3 alerts in 10 min) and the
most-searched device.

**4. Auto mode.** With the toggle on, any suggestion that is tied to a live alert with ≥70% confidence and
≥3 observations is executed automatically by `AutomationLoop`. Auto commands go on the **same undo
stack**, so the operator can still revert them. They are **not** fed back into the pattern table, so the
engine never learns from its own decisions.

**Seeding.** `SeedHistoricalSessions()` loads 19 historical operator sessions at start-up so suggestions
work on a fresh run. Live observations are added on top. The dashboard header shows *"19 seeded + N live
obs."*

**The closed loop you can watch.** In the simulation, Moisture Sensor B slowly dries out until Pump A is
switched on, then rises and overshoots if the pump is left on. So the full story plays out by itself:
trend predicted → alert → "Turn ON Pump A" suggested (or auto-run) → moisture recovers → "moisture high"
→ "Turn OFF Pump A" suggested.

---

## 6. How to test each data structure

Start the API and dashboard (§1) and sign in. 6.1–6.6 use **Tab 2 – Command Stream**, 6.7–6.9 use **Tab 3 – Topology & Mesh**. Everything below can
be done from the dashboard. The `curl` versions are given too (Git Bash / macOS / Linux; on PowerShell
use `curl.exe` and escape quotes, or use the dashboard buttons).

**curl and authentication.** Telemetry is posted the way a device does it, with the fleet's device key
header (`X-Device-Key`, from `Devices:ApiKey` in `appsettings.json`). Every other endpoint needs your
Firebase ID token. While signed in, open the browser dev-tools console on the dashboard and run:

```js
copy(await firebase.auth().currentUser.getIdToken())   // token is now on your clipboard
```

Then in your terminal:

```bash
export TOKEN="<paste>"                       # PowerShell: $env:TOKEN="<paste>"
export DEVICE_KEY="smartx-esp32-fleet-key-2026"
```

### 6.1 Simulate a high-priority alert (PriorityQueue bypass)

**Dashboard:** in the **Simulate** card click **Burst 900 + critical spike**.
Watch the KPI strip and the **Ingestion pipeline** card:

- *Standard queue* jumps to several hundred waiting packets, with an average wait of several seconds.
- The critical 2950 W packet appears in the **Priority lane**, is processed on the next tick (wait in the
  tens of milliseconds), the *bypasses* counter goes up, and a red toast appears.
- In the live packet feed it shows as a **PRIO** row while the FIFO rows around it show multi-second latency.

**curl:**

```bash
# 1. back up the FIFO lane with 900 in-band packets (sent by the simulated fleet over HTTP)
curl -H "Authorization: Bearer $TOKEN" -X POST "http://localhost:5080/api/simulate/burst?count=900"

# 2. immediately send a critical power spike, exactly like a device would
curl -X POST http://localhost:5080/api/telemetry \
  -H "X-Device-Key: $DEVICE_KEY" -H "Content-Type: application/json" \
  -d '{"deviceId":"AA:BB:CC:00:03","metric":"wattage","valueType":"float","rawValue":"2950"}'
# -> {"accepted":true,"severity":"Critical","lane":"priority","queuePosition":1,...}

# 3. compare the two lanes
curl -H "Authorization: Bearer $TOKEN" http://localhost:5080/api/stream/stats
# standard.avgLatencyMs is in seconds, priority.avgLatencyMs is a few ms, priorityBypasses >= 1
```

Tip: drag the **Gateway CPU budget** slider down (e.g. 20 pkt/s) to make the backlog last longer.

Other critical inputs: moisture below 15% (`"metric":"moisture","rawValue":"8.5"`), wattage above 2600 W,
temperature above 50 °C. Out-of-band but not critical (e.g. moisture 18%) goes in the priority lane as a
**Warning**, behind any criticals.

### 6.2 Stack undo / redo

1. Click **On** on the *Pump A* tile → the override appears at the **TOP** of the stack.
2. Click **Shut down all valves** → both valves show **CLOSED** and one record covering 2 devices is pushed.
3. Click **Undo** → both valves are open again (one pop reverted both).
4. Click **Redo** → they close again. Issue any new command and the redo stack is cleared.

```bash
curl -H "Authorization: Bearer $TOKEN" -X POST http://localhost:5080/api/commands -H "Content-Type: application/json" \
  -d '{"type":"TurnOn","targetMac":"AA:BB:CC:00:05"}'
curl -H "Authorization: Bearer $TOKEN" -X POST http://localhost:5080/api/commands -H "Content-Type: application/json" \
  -d '{"type":"ShutDownAllValves"}'
curl -H "Authorization: Bearer $TOKEN" -X POST http://localhost:5080/api/commands/undo
curl -H "Authorization: Bearer $TOKEN" http://localhost:5080/api/commands/history     # undoStack[0] is the next thing Undo will revert
```

Command types: `TurnOn`, `TurnOff`, `Toggle` (need `targetMac`), `ShutDownAllValves`, `OpenAllValves`.

### 6.3 Dictionary lookups, category index and search

- Type `pump`, `Bay 2` or `00:07` in the device search box. Use the category chips and the state chips
  (`Critical`, `Offline`, …) to filter. Filtering runs on the server: the category goes through the
  `Dictionary<Category, HashSet<MAC>>` index first.
- Every packet updates its device through one dictionary lookup. Watch the tiles' *last seen* and
  sparklines update every couple of seconds.

```bash
curl -H "Authorization: Bearer $TOKEN" "http://localhost:5080/api/devices?q=pump"
curl -H "Authorization: Bearer $TOKEN" "http://localhost:5080/api/devices?category=Actuator"
curl -H "Authorization: Bearer $TOKEN" "http://localhost:5080/api/devices?state=Critical"
curl -H "Authorization: Bearer $TOKEN" "http://localhost:5080/api/devices/AA:BB:CC:00:02"          # O(1) by MAC
```

### 6.4 SortedDictionary timeline

Click any sensor tile. The **Timeline** card draws its history, which arrives from the API already in
timestamp order. Switch between **2 / 5 / 10 min** to run a range query. Out-of-band points are annotated
directly on the chart.

```bash
curl -H "Authorization: Bearer $TOKEN" "http://localhost:5080/api/devices/AA:BB:CC:00:02/history?limit=20"
curl -H "Authorization: Bearer $TOKEN" "http://localhost:5080/api/devices/AA:BB:CC:00:02/history?from=2026-10-16T10:00:00Z&to=2026-10-16T10:05:00Z"
```

### 6.5 HashSet duplicate suppression and disconnects

- **Repeated error:** send the same critical reading five times in a row. *Active errors* holds a
  single `wattage-high` entry, while *Duplicates blocked* goes up by 4:

  ```bash
  for i in 1 2 3 4 5; do
    curl -s -X POST http://localhost:5080/api/telemetry -H "X-Device-Key: $DEVICE_KEY" -H "Content-Type: application/json" \
      -d '{"deviceId":"AA:BB:CC:00:03","metric":"wattage","valueType":"float","rawValue":"2950"}'
  done
  curl -H "Authorization: Bearer $TOKEN" http://localhost:5080/api/alerts/active
  ```

  The same thing happens on the dashboard if you click **Critical power spike** a few times quickly.
  The meter's next normal reading resolves the error and removes it from the set.
- **Disconnect:** pick a node in **Simulate** → **Take offline 30s**. After ~12 s it appears in the
  *Disconnected* set and the tile turns grey. The heartbeat monitor keeps re-checking every 2 s, but the
  alert is raised once and the repeats are counted as blocked duplicates. When the node comes back it's
  removed from the set (*reconnected*).

```bash
curl -H "Authorization: Bearer $TOKEN" -X POST "http://localhost:5080/api/simulate/offline/AA:BB:CC:00:07?seconds=30"
curl -H "Authorization: Bearer $TOKEN" http://localhost:5080/api/alerts/active
```

### 6.6 Predictive engine

- **Suggestion from a live alert:** inject a moisture drop on Sensor B and a *"Turn ON Pump A"*
  suggestion appears with its confidence and how many times it was seen.

  ```bash
  curl -X POST http://localhost:5080/api/telemetry -H "X-Device-Key: $DEVICE_KEY" -H "Content-Type: application/json" \
    -d '{"deviceId":"AA:BB:CC:00:02","metric":"moisture","valueType":"float","rawValue":"12"}'
  curl -H "Authorization: Bearer $TOKEN" http://localhost:5080/api/engine/suggestions
  ```

- **Learning from your behaviour:** search for a device, then run a command on another device. Repeat
  this twice and the pair shows up in **Learned patterns**. The next time you search for the first
  device, the second action is suggested.
- **Prediction:** leave the app running. Moisture Sensor B drifts down and a *trend* insight predicts the
  breach about a minute before it happens.
- **Auto mode:** flip the toggle. When Sensor B goes below 20%, the engine turns Pump A on by itself.
  The command appears on the stack as *Auto-Engine* and can be undone.

---

### 6.7 AVL range query (Tab 3 → Historical telemetry log)

**Dashboard:** open **Tab 3 – Topology & Mesh**, scroll to **Historical telemetry log**, pick
*Moisture Sensor B* and click **14:00–16:00**. The stats row shows ~480 readings in range, the tree's size
and height (~11 500 readings, height 14), and how many nodes the query visited (~496, not 11 500). Click
**Compare with list scan** to see the AVL query beside a full scan of an unsorted `List<T>`. The
*tree verified balanced & ordered* tag means `IsValidAvl()` checked the height, balance and BST order of
every node.

```bash
# readings for Sensor B between 14:00 and 16:00 SAST (12:00-14:00 UTC) yesterday - change the date to yesterday's
curl -H "Authorization: Bearer $TOKEN" "http://localhost:5080/api/history/AA:BB:CC:00:02?from=2026-11-09T12:00:00Z&to=2026-11-09T14:00:00Z" | head -c 600
# -> "totalInRange":480, "stats":{"treeSize":11523,"treeHeight":14,...}

curl -H "Authorization: Bearer $TOKEN" "http://localhost:5080/api/history/AA:BB:CC:00:02/benchmark?from=2026-11-09T12:00:00Z&to=2026-11-09T14:00:00Z"
# -> avlNodesVisited ~496 vs listItemsScanned ~11523, speedup ~10, treeIsValidAvl: true
```

### 6.8 Trace a route and watch the mesh self-heal (Graph + Prim's MST)

**Dashboard:** on the mesh map click **Moisture Sensor B** (or type `AA:BB:CC:00:02` into the route
tracer). The green route shows 4 hops to the gateway at 11.42 mJ per packet. Now click **Zone 1 Router** on
the map and press **Simulate failure of this radio**. Within ~3 s the router gets a red X, the MST is
recomputed without it, and Sensor B and Pump A get an orange **re-routed** ring. Trace Sensor B again: it now
goes through Pump A and Irrigation Valve 2. Fail the **Main Board Router** and the Grid Tie meter has no
other neighbour. It shows as **isolated**, stops sending telemetry, and appears on Tab 2 as offline after the
12 s heartbeat timeout. **Restore all radios** puts everything back.

```bash
curl -H "Authorization: Bearer $TOKEN" http://localhost:5080/api/topology/route/AA:BB:CC:00:02
curl -X POST -H "Authorization: Bearer $TOKEN" http://localhost:5080/api/topology/nodes/AA:BB:CC:FF:01/fail
curl -H "Authorization: Bearer $TOKEN" http://localhost:5080/api/topology/route/AA:BB:CC:00:02   # "rerouted": true
curl -X POST -H "Authorization: Bearer $TOKEN" http://localhost:5080/api/topology/restore-all
```

### 6.9 BFS: find which devices in a subnet are still connected

**Dashboard:** with the Zone 1 router failed (6.8), set **Subnet explorer** to *Central Gateway* and
leave **Stay inside this subnet** unticked. BFS lists the radios reached at 1, 2 and 3 hops. Under *Not
reached by this search* the Zone 1 router is shown as **down**, with the devices that used to route
through it and their new next hop. Now pick **Zone 1 Mesh Router** itself. Because it is down, the search
reaches nothing.

```bash
curl -H "Authorization: Bearer $TOKEN" "http://localhost:5080/api/topology/explore/AA:BB:CC:FF:00?subnetOnly=false"
curl -H "Authorization: Bearer $TOKEN" "http://localhost:5080/api/topology/explore/AA:BB:CC:FF:02?subnetOnly=true"
```

### 6.10 Automated tests

`backend/SmartX.Tests` is a small console test runner (no NuGet packages needed) with 31 checks: AVL
height, balance and order after 20 000 sorted inserts, 50 random range queries against a brute-force scan,
MinHeap ordering, BFS hop counts / subnet limits / self-healing, and Prim's MST against an independent
**Kruskal** implementation on 30 random meshes.

```bash
cd backend
dotnet run --project SmartX.Tests
# ... 31 passed, 0 failed   (exit code 0)
```

---

## 7. API reference

All routes except `/`, `/api/health` and `/api/auth/status` need `Authorization: Bearer <Firebase ID token>`.
The device routes (`POST /api/telemetry`, `POST /api/telemetry/batch`, `POST /api/sensors`,
`GET /api/devices/{mac}/shadow`) also accept the fleet's `X-Device-Key` header. The SSE stream takes the
token as `?access_token=` because `EventSource` can't send headers.

### Authentication & import

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/auth/status` | Public: whether `Firebase:ProjectId` is configured |
| GET | `/api/auth/me` | Who the API thinks you are (proves your token was accepted) |
| POST | `/api/devices/import` | Multipart `.txt` device list → registers every device and puts it live |
| GET | `/api/structures` | Every named structure in `SmartXDataStructures` with its live size |

### Final PoE (Tab 3)

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/topology` | Whole mesh: vertices, weighted edges, which edges are in the MST, node status, subnets, stats |
| GET | `/api/topology/route/{mac-or-name}` | MST route to the gateway hop by hop, fewest-hop BFS path, neighbours, re-routed flag |
| GET | `/api/topology/explore/{start}?subnetOnly=true` | BFS from a gateway/router: levels, cut-off radios, where their traffic goes now |
| POST | `/api/topology/nodes/{mac}/fail` · `/restore` | Take a radio down / bring it back |
| POST | `/api/topology/restore-all` | Restore every failed radio |
| GET | `/api/history` | Sensors with an AVL history tree (size, height, first/last reading) |
| GET | `/api/history/{mac}?from=&to=&limit=` | AVL range query (default: last 2 h), with min/max/avg and query stats |
| GET | `/api/history/{mac}/summary` | Tree size, height, rotations, time span |
| GET | `/api/history/{mac}/benchmark?from=&to=` | Same window on the AVL tree vs a `List<T>` scan |

### Part 2

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/telemetry` | Ingest one reading → classified into the FIFO or priority lane (202 Accepted) |
| GET | `/api/devices?q=&category=&state=&track=` | Search / filter live devices (`track=true` records it as a user search) |
| GET | `/api/devices/{mac}` | One device by MAC (dictionary lookup) |
| GET | `/api/devices/{mac}/history?from=&to=&limit=` | Sorted timeline / range query |
| GET | `/api/devices/{mac}/shadow` | Desired actuator state (polled by devices) |
| POST | `/api/commands` | Issue a manual override |
| POST | `/api/commands/undo` · `/redo` | Pop the undo / redo stack |
| GET | `/api/commands/history` | Both stacks and the command log |
| GET | `/api/stream/stats` | Lane depths, latency, throughput, bypasses |
| GET | `/api/stream/queue` | Peek into both lanes |
| GET | `/api/stream/recent` | Last processed packets |
| POST | `/api/stream/rate?perSecond=` | Change the gateway processing budget |
| GET | `/api/alerts/active` | Both HashSets, duplicate count, alert feed |
| GET | `/api/events` | **Server-Sent Events** stream (`packet`, `alert`, `command`, `automation`) |
| GET | `/api/engine/suggestions` · `/insights` · `/patterns` · `/summary` | Predictive engine output |
| POST | `/api/engine/automode?enabled=true` | Toggle auto mode |
| GET | `/api/dashboard` | Everything the Command Stream page needs in one round-trip |
| POST | `/api/simulate/burst?count=` | Fleet fires N in-band packets over HTTP |
| POST | `/api/simulate/offline/{mac}?seconds=` | Make a simulated node go silent |
| GET | `/api/health` | Liveness check |

### Part 1 (unchanged)

| Method | Route | Purpose |
|---|---|---|
| POST / GET | `/api/sensors` | Register / list sensors (`name` is now an optional field) |
| POST / GET | `/api/sensors/{mac}/attachments` | Multipart upload / list attachments |
| POST | `/api/telemetry/batch?deviceId=&metric=` | Jagged-array batch ingest |
| GET | `/api/telemetry/snapshot` · `/api/anomalies` | Part 1 typed store + anomaly log |
| GET / POST | `/api/deployment/seed` · `/api/deployment/validate` | Recursive tree validation |
| GET | `/api/demo/power-aggregate` | Operator overloading demo |

Enums are sent and returned as names (`"Actuator"`, `"Critical"`). Numbers are still accepted on
input, so the Part 1 payloads keep working.

---

## 8. Configuration

`backend/SmartX.Api/appsettings.json` (or environment variables, e.g. `Gateway__ProcessingRatePerSecond=20`):

| Key | Default | Meaning |
|---|---|---|
| `Gateway:ProcessingRatePerSecond` | 60 | Packets the processor handles per second (also adjustable live from the dashboard) |
| `Gateway:HeartbeatTimeoutSeconds` | 12 | Silence before a node counts as disconnected |
| `Simulator:Enabled` | true | Run the built-in device fleet |
| `Simulator:TargetUrl` | *(auto)* | Where the fleet posts. By default it uses the API's own address. |

The frontend calls `http://localhost:5080`. To point it elsewhere, set
`window.SMARTX_API_BASE = "http://host:port"` before the scripts in `index.html`.

### Simulated fleet

| MAC | Device | Category | Publishes |
|---|---|---|---|
| AA:BB:CC:00:01 | Moisture Sensor A | Environmental | moisture % (float), occasional dry spikes |
| AA:BB:CC:00:02 | Moisture Sensor B | Environmental | moisture % — dries out until Pump A runs |
| AA:BB:CC:00:03 | Smart Meter – Utility Room | Power | wattage (float), +1450 W while Smart Relay 1 is on |
| AA:BB:CC:00:04 | Greenhouse Valve | Actuator | heartbeat |
| AA:BB:CC:00:05 | Pump A | Actuator | heartbeat; reads its shadow |
| AA:BB:CC:00:06 | Smart Relay 1 | Actuator | heartbeat; reads its shadow |
| AA:BB:CC:00:07 | Temperature Node C | Environmental | temperature °C (float) |
| AA:BB:CC:00:08 | Smart Meter – Grid Tie | Power | wattage (**int**) |
| AA:BB:CC:00:09 | Irrigation Valve 2 | Actuator | heartbeat |

Every ~2.5 minutes the fleet also takes Sensor A or Temperature Node C offline for 25 s, so the
disconnect handling is exercised without any clicks.

---

## 9. Part 1 concepts (still in place)

- **Generics:** `Models/TelemetryPacket.cs` — `TelemetryPacket<T>` where `T : struct, IComparable<T>`.
  The processor still calls the typed `TelemetryStore.Ingest` overloads, so readings are never boxed.
- **Operator overloading:** `Models/PowerReading.cs` (`+ - > < >= <=`), `GET /api/demo/power-aggregate`.
- **Jagged arrays → List\<T\>:** `TelemetryStore.IngestRawBatches`.
- **Recursion:** `Services/DeploymentValidator.cs` walks Facility → Zone → Sub-Zone → Node.

---

## 10. Troubleshooting

| Problem | Fix |
|---|---|
| Login page says Firebase isn't configured | Fill in `frontend/firebase-config.js` and reload (§0). |
| Signed in but every panel is empty / 401 | Set `Firebase:ProjectId` in `appsettings.json` to the same project and restart the API. |
| "This sign-in method isn't enabled" | Enable Email/Password under Authentication → Sign-in method. |
| Dashboard shows "API offline" | Make sure the API is running on port 5080 (`curl http://localhost:5080/api/health`). |
| Blank page | Serve the `frontend` folder over http (`python -m http.server 5173`). Don't open the file directly. Check that the browser can reach cdnjs. |
| Port 5080 in use | `dotnet run --urls http://localhost:5090`, then set `window.SMARTX_API_BASE` to match. |
| No live data | Check `Simulator:Enabled` is `true`. The API log shows *"Device simulator publishing to …"* on start-up. |
| Live stream says "Reconnecting" | The SSE connection retries every 3 s on its own. Polling still keeps the page up to date in the meantime. |

---

## 11. Authentication & device import

### How sign-in is enforced

- **Frontend** (`frontend/auth.jsx`): the Firebase Auth SDK handles email/password sign-up and sign-in,
  password reset and Google sign-in. Until someone is signed in, only the landing page and login page
  render. Nothing else is mounted, so no data is requested.
- **Every API call** (`api()` in `frontend/shared.jsx`) attaches the user's Firebase **ID token**.
  The SDK refreshes it automatically before it expires.
- **The API** (`Services/FirebaseAuth.cs`) verifies each token itself. It downloads Google's public
  signing keys, checks the RS256 signature, then checks that `aud` equals your project ID,
  `iss = https://securetoken.google.com/<project>`, the token isn't expired, and `sub` (the user's UID)
  is set. Anything else gets a **401**. There are no usernames or passwords stored in this project.
- **Devices** don't sign in as people. The simulated ESP32 fleet sends an `X-Device-Key` header,
  which is only accepted on the device endpoints (telemetry, provisioning, device shadow).

### Importing a device list (Tab 1)

Use **Import device list** on Tab 1 with a `.txt` file, one device per line:

```text
# MAC, Name, Location, Category   (comma, pipe, semicolon or tab separated; '#' = comment)
AA:BB:CC:01:01, Moisture Sensor D, Facility B / Zone 3 / Hydroponics Bay 4, Environmental
AA:BB:CC:01:04, Smart Meter - Cold Room, Facility B / Zone 4 / Cold Room, Power
AA:BB:CC:01:05, Pump B, Facility B / Zone 3 / Pump House, Actuator
```

A ready-made file is at **`frontend/assets/sample-devices.txt`**. You can also use the **Download sample
list** link next to the uploader.

When the file is uploaded, the API (`Services/DeviceListImporter.cs`):

1. reads it line by line, validates each MAC or node ID, and skips duplicates and bad lines with a reason;
2. registers every device in the Part 1 sensor registry, `DeviceRegistry` and `CategoryIndex`;
3. tells the simulator about the new devices, so they **start publishing telemetry over HTTP** within a
   few seconds.

The result card lists what was added, updated and skipped. The new devices then appear in the gauges,
in the registered-sensor list, in the Tab 2 grid and search, and in the category filter counts, all
without a page reload.

```bash
curl -H "Authorization: Bearer $TOKEN" -F file=@frontend/assets/sample-devices.txt http://localhost:5080/api/devices/import
```

---

## 12. Project report, Docker and commit history

- **Project completion report:** `docs/PROG7312_ST10457186_Final_PoE_Report.pdf` (also as `.docx`). It covers the
  challenges in each task (especially Task 3) and how they were solved, key learnings, and justified
  technology recommendations for the .NET stack.
- **Changes from lecturer feedback:** `FEEDBACK_UPDATES.md`.
- **Docker:** `Dockerfile` (API, multi-stage .NET 10 build), `frontend/Dockerfile` (nginx),
  `docker-compose.yml` (both) and `.dockerignore`. Run with `docker compose up --build` (§1, Option B).
- **Tests:** `dotnet run --project backend/SmartX.Tests` (§6.10).
- **Video demonstration:** *[https://youtu.be/y_JdgqmYe68?si=zmzKhtSXCd9BCoxT]*.

---

## 13. References



### Use of generative AI

Claude, an AI assistant made by Anthropic, was used during Part 2 and the final PoE to help program and
debug code (backend services, data-structure refactoring, Firebase token verification, the AVL tree,
mesh graph, BFS and Prim's MST, the test runner and the frontend views), to diagnose GitHub Classroom /
git issues, and to draft this README . All output was reviewed, run and tested by
me before submission.

### Reference list

Adelson-Velsky, G.M. and Landis, E.M. (1962) 'An algorithm for the organization of information', *Soviet Mathematics Doklady*, 3, pp. 1259–1263.

Anthropic (2026) *Claude* (Opus 5.5) [Large language model]. Available at: https://claude.ai (Accessed: 10 November 2026).

Ceccarini, C., Mirri, S., Salomoni, P. and Prandi, C. (2021) 'On exploiting Data Visualization and IoT for Increasing Sustainability and Safety in a Smart Campus', *Mobile Networks and Applications*. Available at: https://doi.org/10.1007/s11036-021-01742-4 (Accessed: 18 October 2026).

Cormen, T.H., Leiserson, C.E., Rivest, R.L. and Stein, C. (2022) *Introduction to Algorithms*. 4th edn. Cambridge, MA: MIT Press.

Docker Inc. (n.d.) *Multi-stage builds*. Available at: https://docs.docker.com/build/building/multi-stage/ (Accessed: 10 November 2026).

Espressif Systems (n.d.) *ESP-WIFI-MESH*. Available at: https://docs.espressif.com/projects/esp-idf/en/latest/esp32/api-guides/esp-wifi-mesh.html (Accessed: 10 November 2026).

Firebase (n.d.a) *Authenticate with Firebase using password-based accounts using JavaScript*. Available at: https://firebase.google.com/docs/auth/web/password-auth (Accessed: 18 October 2026).

Firebase (n.d.b) *Verify ID tokens*. Available at: https://firebase.google.com/docs/auth/admin/verify-id-tokens (Accessed: 18 October 2026).

Jones, M., Bradley, J. and Sakimura, N. (2015) *JSON Web Token (JWT)*. RFC 7519. Internet Engineering Task Force. Available at: https://www.rfc-editor.org/rfc/rfc7519 (Accessed: 18 October 2026).

Kruskal, J.B. (1956) 'On the shortest spanning subtree of a graph and the traveling salesman problem', *Proceedings of the American Mathematical Society*, 7(1), pp. 48–50.

Meta Open Source (n.d.) *React*. Available at: https://react.dev/ (Accessed: 18 October 2026).

Microsoft (n.d.a) *Background tasks with hosted services in ASP.NET Core*. Available at: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services (Accessed: 18 October 2026).

Microsoft (n.d.b) *Create responses in Minimal API applications*. Available at: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/responses (Accessed: 18 October 2026).

Microsoft (n.d.c) *Dictionary<TKey,TValue> Class*. Available at: https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2 (Accessed: 18 October 2026).

Microsoft (n.d.d) *HashSet<T> Class*. Available at: https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.hashset-1 (Accessed: 18 October 2026).

Microsoft (n.d.e) *Minimal APIs overview*. Available at: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis (Accessed: 18 October 2026).

Microsoft (n.d.f) *PriorityQueue<TElement,TPriority> Class*. Available at: https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.priorityqueue-2 (Accessed: 18 October 2026).

Microsoft (n.d.g) *Queue<T> Class*. Available at: https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.queue-1 (Accessed: 18 October 2026).

Microsoft (n.d.h) *SortedDictionary<TKey,TValue> Class*. Available at: https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.sorteddictionary-2 (Accessed: 18 October 2026).

Microsoft (n.d.i) *Stack<T> Class*. Available at: https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.stack-1 (Accessed: 18 October 2026).

Mozilla (n.d.) *Using server-sent events*. MDN Web Docs. Available at: https://developer.mozilla.org/en-US/docs/Web/API/Server-sent_events/Using_server-sent_events (Accessed: 18 October 2026).

Prim, R.C. (1957) 'Shortest connection networks and some generalizations', *Bell System Technical Journal*, 36(6), pp. 1389–1401.

Rappaport, T.S. (2002) *Wireless Communications: Principles and Practice*. 2nd edn. Upper Saddle River, NJ: Prentice Hall.

Sedgewick, R. and Wayne, K. (2011) *Algorithms*. 4th edn. Upper Saddle River, NJ: Addison-Wesley.

The Independent Institute of Education (2026) *PROG7312 Programming 3B: Portfolio of Evidence – Smart-X IoT Mesh Ecosystem*. Johannesburg: The IIE. Unpublished module brief.

Xiao, R., Wu, Z. and Hamari, J. (2021) 'Internet-of-Gamification: A Review of Literature on IoT-enabled Gamification for User Engagement', *International Journal of Human–Computer Interaction*, 38(12), pp. 1113–1137. Available at: https://doi.org/10.1080/10447318.2021.1990517 (Accessed: 18 October 2026).
