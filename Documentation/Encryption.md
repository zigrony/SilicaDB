ENCRYPTION NOTE — AES-CTR tradeoffs and operational guidance
===========================================================

Summary
-------
This codebase uses AES-CTR to encrypt page payloads. AES-CTR provides confidentiality (it hides plaintext content) but does not provide cryptographic integrity or authentication. In other words, AES-CTR alone does NOT detect or prevent tampering.

Key points
----------
- Confidentiality only: AES-CTR prevents reading plaintext without the key, but an attacker who can modify ciphertext may force predictable changes to decrypted plaintext.
- No MAC or AEAD: There is no cryptographic message authentication (HMAC, AES-GCM, or other AEAD) applied to encrypted pages. CRC/CRC32C checks in the code detect accidental corruption but are not cryptographically secure.
- Keystream reuse risk: Reusing the same (key, counter) pair across different plaintexts results in catastrophic disclosure. Counter derivation is implemented to avoid straightforward reuse, but operational errors (restores, clones, incorrect salt/key handling) can reintroduce reuse.

Operational recommendations
---------------------------
1. Treat current encryption as confidentiality-only. Do not rely on it for tamper detection or authenticated recovery.
2. Add an authenticated integrity layer for any scenario that requires:
   - Detecting active tampering by an adversary,
   - Ensuring WAL and checkpoint integrity in adversarial environments,
   - Verifying backups/replicas for tamper/fault resistance.
   Options:
   - Use AEAD per-frame (recommended): AES-GCM or ChaCha20-Poly1305 with unique nonces per frame.
   - Or append an HMAC (e.g., HMAC-SHA256) computed over (header || ciphertext) using a separate integrity key.
3. Manage keys and salts carefully:
   - Key rotation must be planned to avoid keystream reuse and to allow recovery.
   - Device salt must be stable for block counters to behave as designed; document backup/restore behavior.
4. Document safe operational procedures:
   - How to take and restore backups safely without introducing keystream reuse.
   - How to rotate keys and re-encrypt pages or mark affected ranges.
5. If you accept the confidentiality-only model for deployment, make that explicit in docs and operator runbooks.

Short-term mitigations (minimum)
--------------------------------
- Add a prominent doc (this file) to the repo root and to the operator handbook stating the above tradeoffs.
- Add a brief CI check that fails if any code paths assume integrity from encryption alone (search for comments or assertions that treat encryption as authenticated).
- Consider adding a single-page security note in SECURITY.md explaining the threat model and recommended next steps for projects needing integrity.

Suggested PR description to commit this doc
-------------------------------------------
- Add docs/ENCRYPTION.md describing AES-CTR confidentiality-only semantics and operational guidance.
- Add a brief note in README.md pointing to docs/ENCRYPTION.md.
- Add a CI lint that warns if any future code introduces verification logic that treats CRC as sufficient for adversarial integrity.

Rationale and next steps
------------------------
- If confidentiality is sufficient for your environment (trusted storage, no risk of active tampering), this design may be acceptable.
- If you need authenticated integrity (typical for hostile hosts, multi-tenant storage, or untrusted backup/restore), implement AEAD or HMAC and update the WAL/flush/write paths to compute and verify authentication tags on writes and reads.

END OF ENCRYPTION NOTE
