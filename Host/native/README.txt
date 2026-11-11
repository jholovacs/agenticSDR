Bundled Windows x64 natives (committed; copied next to the host on build/publish):

m17voice.dll — M17 frame decode + Codec2 speech in one MIT-friendly DLL.
  No separate libcodec2.dll / m17.dll is required at runtime.
  Rebuild (needs Rust):
    cd native/m17voice
    cargo build --release
    copy target\release\m17voice.dll ..\..\Host\native\m17voice.dll

xaramonitor.dll — MIT xara VARA HF receive monitor (VarAC).
  Rebuild:
    cd native/xaramonitor
    cargo build --release
    copy target\release\xaramonitor.dll ..\..\Host\native\xaramonitor.dll

GPL librtlsdr is not here. The MSI/deb download it, or run tools/fetch-native.sh
into the repo-root native/ folder (gitignored).
