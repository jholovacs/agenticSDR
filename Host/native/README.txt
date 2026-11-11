Bundled MIT natives (copied next to the host on build/publish):

  m17voice — M17 frame decode + Codec2 speech (no separate libcodec2/m17 needed)
  xaramonitor — VARA HF receive monitor for VarAC (MIT xara)

Windows x64 DLLs are committed here for MSI/dev convenience. Linux/macOS CI and
packages rebuild from source:

  bash tools/build-native.sh

Sources live in native/m17voice, native/xaramonitor, and native/libm17 (GPL-2 C
stack linked into m17voice).

GPL librtlsdr for Windows lives in the repo-root native/ folder as rtlsdr.dll
(with pthreadVC2.dll and msvcr100.dll). Refresh with tools/fetch-native.sh.
The MSI copies those next to the host. Linux/macOS use the system librtlsdr package.
