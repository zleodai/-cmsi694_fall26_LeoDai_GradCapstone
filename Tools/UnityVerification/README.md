# Unity adapter verification

Run `python run.py --unity "C:/Program Files/Unity/Hub/Editor/6000.6.0f1/Editor/Unity.exe" --work-dir "C:/Temp/PlaytestOpsVerification"` from this folder, using a **new empty folder** for the project. The runner creates an isolated project, imports only the package's smoke samples, and invokes a verification-only Editor fixture. It never opens or edits a gameplay project.

The fixture verifies:

- UI Toolkit window construction; closing it before tests does not stop the service.
- Six discovered cases across EditMode and PlayMode.
- Missing/duplicate/ambiguous selectors are rejected.
- Normal EditMode execution and exact parameterized-case selection.
- Deliberately selected assertion failure with message and stack trace.
- A selected PlayMode case completes across domain reload while another PlayMode case exists.
- Every final NUnit XML contains exactly one test case, matching the requested full name.
- Structured JSON and NUnit XML are both written.

The intentional failed assertion is **expected evidence**, not a failed verification suite. Success is `verification-passed.json` plus a zero Editor exit code. Logs and `verified-0.json` through `verified-3.json` remain in the isolated folder. The script has a bounded timeout and never terminates a pre-existing Editor process.

These checks are real Unity execution. They do not establish visual layout quality, gameplay correctness, no-domain-reload settings, idle CPU/allocation measurements, connectivity, dashboard behavior, physical phone access, or VM behavior.
