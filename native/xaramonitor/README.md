# xaramonitor

Receive-only VARA HF monitor embedded for AgenticSDR.

Built from MIT **xara** (`xara-phy`, `xara-arq`, and the passive `monitor`) by x0rro.
Upstream distribution of the full modem was discontinued; this tree keeps the
decode path we need and exposes a small C ABI.

## Build (needs Rust 1.85+)

```bash
cd native/xaramonitor
cargo build --release
cp target/release/xaramonitor.dll ../
cp target/release/xaramonitor.dll ../../Host/native/
```

On Linux/macOS the shared library is `libxaramonitor.so` / `libxaramonitor.dylib`.

## C ABI

- `xaramonitor_create(bandwidth_hz, sample_rate)`
- `xaramonitor_push_f32(handle, samples, count)`
- `xaramonitor_pop(handle, buf, cap)`
- `xaramonitor_finish(handle)`
- `xaramonitor_destroy(handle)`

Audio is float mono in about −1…1. Non-48 kHz rates are resampled inside the library.
