m17voice.dll — M17 frame decode (libm17) and Codec2 speech for the host.

Rebuild (needs Rust):
  cd native/m17voice
  cargo build --release
  copy target\release\m17voice.dll ..\..\Host\native\m17voice.dll
