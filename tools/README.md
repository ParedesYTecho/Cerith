# Tools

`check_grok_billing_protocol.py` qualifies an explicitly pinned Windows Grok binary
using synthetic credentials, an isolated profile and a loopback billing backend.
It checks `_x.ai/billing` rejection before backend access and whether auth/info
observes a synthetic owner switch within three seconds. It never exports a bearer
or sends a model/session request. Exit zero requires both checks for both auth cases;
a nonzero exit is a failed qualification, not a live-account result.

```powershell
python tools/check_grok_billing_protocol.py --binary <official-grok.exe> --sha256 <verified-sha256> --version <exact-version>
```

This manual development probe needs only Python's standard library and the
explicitly selected binary; neither is a new Cerith runtime dependency.
Version/hash verification and synthetic fixtures do not prove signed-in quota
semantics, renewal, persistence safety or safe termination during refresh.
