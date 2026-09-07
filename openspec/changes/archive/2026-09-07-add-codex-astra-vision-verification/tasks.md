## 1. Real-client vision coverage

- [x] 1.1 Extend the app-server driver with native first-turn local-image input using the documented protocol.
- [x] 1.2 Add randomized PNG challenges, persistent visual evidence, and separate attached-image and `view_image` behavior cases through the stock Astra route.
- [x] 1.3 Add failing contract tests and a sanitized real-client fixture for native tool-output image detection; fix the missing vision flag without rewriting opaque content.

## 2. Verification and documentation

- [x] 2.1 Document both cases and their verdict criteria in the mirrored real-client skill references and harness instructions.
- [x] 2.2 Build the harness, run applicable unit and skill-compatibility checks, and validate OpenSpec strictly.
- [x] 2.3 Execute both real Codex cases against live Astra; inspect image transport, matching tools, exact visual answers, and each client's own SQLite log; record the verdict and evidence paths.
- [x] 2.4 Replay the captured image-output request through the real bridge endpoint and confirm image fidelity and the vision header.
