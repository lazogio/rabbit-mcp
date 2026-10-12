# Bugs (newest first)

Next ID: BUG-002

### BUG-001 CI warned that GitHub Actions run on deprecated Node 20 — fixed (2026-10-12)
- Symptom: first CI run (commit ee0db8b) green but annotated: actions/checkout@v4, setup-dotnet@v4,
  upload-artifact@v4 target Node 20 and were forced onto Node 24.
- Root cause: workflow pinned to older action majors.
- Fix: checkout@v7, setup-dotnet@v6, upload-artifact@v7 — commit 84e14d7. Next run had no deprecation annotation.
- Guard: none automatic; `ci-doctor` checks run annotations after each push.
- Lesson: after adding/changing a workflow, read the job annotations, not only the conclusion.
