Bundled MIT natives (copied next to the host on build/publish):

  m17voice — M17 frame decode + Codec2 speech (no separate libcodec2/m17 needed)
  xaramonitor — VARA HF receive monitor for VarAC (MIT xara)

Windows x64 DLLs are committed here for MSI/dev convenience. Linux/macOS CI and
packages rebuild from source:

  bash tools/build-native.sh

Sources live in native/m17voice, native/xaramonitor, and native/libm17 (GPL-2 C
stack linked into m17voice). GPL librtlsdr is not here — the MSI/deb download it,
or run tools/fetch-native.sh into the repo-root native/ folder (that fetch clutter
stays gitignored).
