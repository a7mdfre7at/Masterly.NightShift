# Changelog

## 1.0.0

- Initial release of Masterly.NightShift.
- Packages: `Masterly.NightShift`, `Masterly.NightShift.Core`, `Masterly.NightShift.SqlServer`, `Masterly.NightShift.NetCore`, `Masterly.NightShift.AspNetCore`.
- Targets `netstandard2.0`, `net8.0` and `net10.0`.
- JSON through System.Text.Json; no Newtonsoft.Json dependency. `UseSerializerSettings` and `UseRecommendedSerializerSettings` take `JsonSerializerOptions`. Data written by Newtonsoft-based versions, including `$type` states and arguments, stays readable.
- Reflection-free job invocation, interceptors, concurrency control, batches, fan-in continuations, run windows, a redesigned dashboard with live monitoring and insights, drain mode, Prometheus metrics, alerts, an audit log and OpenTelemetry-compatible telemetry. See `docs/whats-new-next.md`.
