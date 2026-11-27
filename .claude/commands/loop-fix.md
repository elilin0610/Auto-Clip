---
description: 規劃 → 實作 → 驗收 → 不過就重做，直到 harness 通過 / plan → implement → verify → redo until the harness passes
argument-hint: <要做的事 / the task>
---

任務 / Task: $ARGUMENTS

照 CLAUDE.md 的 loop 流程做 / Follow the loop in CLAUDE.md:

1. 先寫出計畫和「怎樣算完成」，包括要新增或要通過的單元測試、golden 錄影。
   Write the plan and the definition of done, including the unit tests and golden recordings that must pass.
2. 實作。/ Implement.
3. 跑 `powershell -NoProfile -ExecutionPolicy RemoteSigned -File harness/verify.ps1`。
   Run the harness.
4. 沒過就讀 `harness/out/report.md`，修根本原因，回到第 3 步，最多 5 輪。不可放寬測試或期望值。
   On failure read the report, fix the root cause, repeat up to 5 rounds. Never weaken tests or expectations.
5. 最後回報每一輪改了什麼、最後的報告結果。
   Finally report what each round changed and the final report.
