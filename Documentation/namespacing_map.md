# 🧭 SilicaDB Namespace Map

This document defines how each subsystem in SilicaDB is organized by namespace. It establishes boundaries for internal vs public APIs, lifecycle ownership, and modular clarity.

---

## 📦 Root Namespace

- `SilicaDB`
  - Shared types, configuration classes, global constants.
  - Entry points and coordination surfaces.
  - Minimal logic — acts as dispatcher or orchestrator only.

---

## 📚 Parser

- `SilicaDB.Parser`
  - SQL Lexer and Parser
  - AST Node Definitions
  - Parser Exceptions and Diagnostics

---

## 🗃 StorageEngine

- `SilicaDB.StorageEngine`
  - Sub-namespaces:
    - `SilicaDB.StorageEngine.PageManagement` — page formats, allocation
    - `SilicaDB.StorageEngine.Buffering` — buffer pool, eviction policies
    - `SilicaDB.StorageEngine.Persistence` — file I/O, durability
    - `SilicaDB.StorageEngine.Indexing` — B+Tree, LSM structures

---

## 💾 TransactionManager

- `SilicaDB.Transactions`
  - Sub-namespaces:
    - `SilicaDB.Transactions.Context` — transaction lifecycles
    - `SilicaDB.Transactions.Locking` — locks, atomicity enforcement
    - `SilicaDB.Transactions.Isolation` — MVCC, snapshot management
    - `SilicaDB.Transactions.Consistency` — write-ahead logging, recovery

---

## 📑 Catalog

- `SilicaDB.Catalog`
  - Schema tracking and metadata
  - Enumeration and lookups
  - DDL lifecycle handling

---

## 🌐 Networking

- `SilicaDB.Networking`
  - RPC message formats
  - Connection and message handling
  - Protocol serialization and deserialization

---

## 🧭 Consensus & Cluster Coordination *(future subsystem)*

- `SilicaDB.Cluster`
  - Raft node state machines
  - Replication and leader election
  - Membership metadata

---

## 🔐 Auth & Security *(optional)*

- `SilicaDB.Security`
  - Authentication and role-based access
  - Network encryption (TLS, certs)

---

## 🧪 Testing Internals

- `SilicaDB.Tests`
  - Test doubles and subsystem mocks
  - Internal lifecycle tracing

---

## 🧩 Rules

- Use `internal` modifiers for helper classes inside sub-namespaces.
- Public APIs live only at the main namespace level for each subsystem.
- Avoid inter-subsystem calls without clearly defined interfaces.
- All disposal and ownership contracts must stay inside their home namespace unless elevated explicitly.
