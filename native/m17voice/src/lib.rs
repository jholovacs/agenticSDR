//! M17 frame decode (libm17) plus Codec2 speech for the Agentic SDR host.

use codec2::{Codec2, Codec2Mode};
use std::os::raw::{c_char, c_float, c_int, c_uint, c_ushort};
use std::slice;

#[repr(C)]
pub struct Lsf {
    pub dst: [u8; 6],
    pub src: [u8; 6],
    pub type_field: [u8; 2],
    pub meta: [u8; 14],
    pub crc: [u8; 2],
}

extern "C" {
    fn decode_LSF(lsf: *mut Lsf, pld_symbs: *const c_float) -> c_uint;
    fn decode_str_frame(
        frame_data: *mut u8,
        lich: *mut u8,
        fn_: *mut c_ushort,
        lich_cnt: *mut u8,
        pld_symbs: *const c_float,
    ) -> c_uint;
    fn decode_callsign_bytes(outp: *mut c_char, inp: *const u8);
    fn slice_symbols(out: *mut c_ushort, inp: *const c_float);
}

#[no_mangle]
pub unsafe extern "C" fn m17_decode_lsf(lsf: *mut Lsf, pld_symbs: *const c_float) -> c_uint {
    decode_LSF(lsf, pld_symbs)
}

/// Decode an LSF into a flat 30-byte buffer: DST(6) SRC(6) TYPE(2) META(14) CRC(2).
#[no_mangle]
pub unsafe extern "C" fn m17_decode_lsf_bytes(out30: *mut u8, pld_symbs: *const c_float) -> c_uint {
    let mut lsf = Lsf {
        dst: [0; 6],
        src: [0; 6],
        type_field: [0; 2],
        meta: [0; 14],
        crc: [0; 2],
    };
    let eucl = decode_LSF(&mut lsf, pld_symbs);
    if out30.is_null() {
        return eucl;
    }
    let out = slice::from_raw_parts_mut(out30, 30);
    out[0..6].copy_from_slice(&lsf.dst);
    out[6..12].copy_from_slice(&lsf.src);
    out[12..14].copy_from_slice(&lsf.type_field);
    out[14..28].copy_from_slice(&lsf.meta);
    out[28..30].copy_from_slice(&lsf.crc);
    eucl
}

#[no_mangle]
pub unsafe extern "C" fn m17_decode_stream(
    frame_data: *mut u8,
    lich: *mut u8,
    frame_number: *mut c_ushort,
    lich_cnt: *mut u8,
    pld_symbs: *const c_float,
) -> c_uint {
    decode_str_frame(frame_data, lich, frame_number, lich_cnt, pld_symbs)
}

#[no_mangle]
pub unsafe extern "C" fn m17_callsign(outp: *mut c_char, inp: *const u8) {
    decode_callsign_bytes(outp, inp)
}

#[no_mangle]
pub unsafe extern "C" fn m17_slice_symbols(out: *mut c_ushort, inp: *const c_float) {
    slice_symbols(out, inp)
}

#[no_mangle]
pub extern "C" fn m17voice_create(mode_bps: c_int) -> *mut Codec2 {
    let mode = match mode_bps {
        1600 => Codec2Mode::MODE_1600,
        _ => Codec2Mode::MODE_3200,
    };
    Box::into_raw(Box::new(Codec2::new(mode)))
}

#[no_mangle]
pub unsafe extern "C" fn m17voice_destroy(handle: *mut Codec2) {
    if !handle.is_null() {
        drop(Box::from_raw(handle));
    }
}

#[no_mangle]
pub unsafe extern "C" fn m17voice_samples_per_frame(handle: *mut Codec2) -> c_int {
    if handle.is_null() {
        return 0;
    }
    (*handle).samples_per_frame() as c_int
}

#[no_mangle]
pub unsafe extern "C" fn m17voice_decode(
    handle: *mut Codec2,
    pcm_out: *mut i16,
    bits: *const u8,
) -> c_int {
    if handle.is_null() || pcm_out.is_null() || bits.is_null() {
        return -1;
    }
    let codec = &mut *handle;
    let samples = codec.samples_per_frame();
    let bytes = (codec.bits_per_frame() + 7) / 8;
    let out = slice::from_raw_parts_mut(pcm_out, samples);
    let frame = slice::from_raw_parts(bits, bytes);
    codec.decode(out, frame);
    samples as c_int
}
