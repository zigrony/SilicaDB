# 🏗️ SilicaDB Documentation

SilicaDB is a custom-built, distributed relational database engine architected for correctness, explicit ownership, and concurrency-safe design. This documentation serves as the technical nucleus for its internal subsystems, design contracts, and lifecycle principles.

---

## 🧠 Why SilicaDB?

Modern embedded databases often hide complexity behind abstractions — sometimes at the cost of predictability, traceability, and leak-free behavior. SilicaDB challenges that norm by making everything **explicit**:
- Every resource has a lifecycle.
- Every subsystem has an owner.
- Every concurrent access path is accounted for.

It's designed not just to work — but to withstand scrutiny, refactoring, and future extensibility under concurrent load.

---

## 🌍 Distributed by Design

SilicaDB is not a single-node toy — it's built to scale across machines and handle state coordination. Future versions will support:
- Raft-based consensus for metadata and leadership
- Cluster membership tracking
- Replicated durability guarantees
- Eventually: sharded physical storage and query routing

---

## 🔌 Interfaces & Integration

SilicaDB exposes a modular interface layer, designed for protocol flexibility. The default implementation will expose at least:
- 📡 **HTTP endpoints** for query submission and admin control
- 🔁 Interface factories for swapping protocols (e.g. REST, gRPC, custom binary)
- 🔧 Pluggable request serialization via strategy pattern

Internals never depend on any specific interface — they only bind to the exposed service layer via contracts.

---

## 🔩 Architectural Goals

- ✅ **Deterministic Shutdown**: All components implement testable, nested disposal contracts.
- 🧹 **Lifecycle Hygiene**: No leaks. No phantom ownership. Cleanup is never implicit.
- 🔁 **Concurrency Correctness**: Locks, atomics, and async paths are auditably safe.
- 🔒 **Explicit Ownership**: Every subsystem knows what it owns, and what it merely references.
- 🔍 **Traceability Over Magic**: No hidden threads, caches, or background daemons. Everything is observable.
- 🔗 **Vertical Segmentation**: Namespaces are tightly scoped, with internal contracts and minimal bleeding.

---

## 🧭 Where to Start?

- 📖 [`namespacing_map.md`](./namespacing_map.md) — Defines subsystem boundaries and how namespaces are organized.
- ⏳ `lifecycle_contracts.md` *(WIP)* — Maps disposal and init sequences for every module.
- 🔒 `concurrency_audit_log.md` *(WIP)* — Tracks concurrency bugs and thread-safety improvements.

---

## 🚧 Work In Progress

SilicaDB is actively evolving. Upcoming efforts include:
- Buffer pool eviction policy refinements
- Snapshot isolation tracing
- WAL performance profiling
- HTTP/gRPC interface scaffolding
- Cluster coordination and replication
- Fine-grained metrics and tracing

---

Built for engineers who don’t just want code that compiles — but code that can be proven correct.
