fn main() {
    let m17 = std::path::Path::new("../libm17");
    println!("cargo:rerun-if-changed={}", m17.join("m17.c").display());
    println!("cargo:rerun-if-changed={}", m17.join("m17.h").display());

    let mut build = cc::Build::new();
    build
        .include(m17)
        .file(m17.join("encode/convol.c"))
        .file(m17.join("encode/symbols.c"))
        .file(m17.join("decode/viterbi.c"))
        .file(m17.join("decode/symbols.c"))
        .file(m17.join("phy/sync.c"))
        .file(m17.join("phy/slice.c"))
        .file(m17.join("phy/interleave.c"))
        .file(m17.join("phy/randomize.c"))
        .file(m17.join("math/rrc.c"))
        .file(m17.join("math/math.c"))
        .file(m17.join("math/golay.c"))
        .file(m17.join("payload/lich.c"))
        .file(m17.join("payload/call.c"))
        .file(m17.join("payload/crc.c"))
        .file(m17.join("payload/lsf.c"))
        .file(m17.join("m17.c"))
        .define("NDEBUG", None)
        .warnings(false);

    if cfg!(target_env = "msvc") {
        build.flag("/std:c11");
    } else {
        build.std("c11");
    }

    build.compile("m17");
}
