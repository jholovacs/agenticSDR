//! C ABI around the MIT xara VARA HF passive monitor (receive only).
mod monitor;
mod resample;

use std::os::raw::{c_char, c_int};
use std::ptr;
use monitor::{Monitor, MonitorOpts};
use resample::Resampler;
use xara_phy::hf::level::Bandwidth;

pub struct Handle {
    mon: Monitor,
    resampler: Option<Resampler>,
    finished: bool,
}

fn bw_from_hz(hz: u32) -> Bandwidth {
    match hz {
        500 => Bandwidth::Hz500,
        2750 => Bandwidth::Hz2750,
        _ => Bandwidth::Hz2300,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn xaramonitor_create(bandwidth_hz: u32, sample_rate: u32) -> *mut Handle {
    let bw = bw_from_hz(bandwidth_hz);
    let rate = if sample_rate < 1_000 { 48_000 } else { sample_rate };
    let resampler = if rate == 48_000 {
        None
    } else {
        match Resampler::new(f64::from(rate), 48_000.0) {
            Ok(r) => Some(r),
            Err(_) => return ptr::null_mut(),
        }
    };
    let mon = Monitor::new(bw, &MonitorOpts::default());
    Box::into_raw(Box::new(Handle { mon, resampler, finished: false }))
}

/// # Safety
/// `h` from create; `samples` length `count`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn xaramonitor_push_f32(h: *mut Handle, samples: *const f32, count: usize) -> c_int {
    if h.is_null() || samples.is_null() || count == 0 {
        return 0;
    }
    let handle = unsafe { &mut *h };
    if handle.finished {
        return 0;
    }
    let slice = unsafe { std::slice::from_raw_parts(samples, count) };
    let mut f64s: Vec<f64> = slice.iter().map(|&s| f64::from(s)).collect();
    if let Some(r) = &mut handle.resampler {
        f64s = r.process(&f64s);
    }
    if !f64s.is_empty() {
        handle.mon.feed(&f64s);
    }
    1
}

/// # Safety
/// `buf` has `cap` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn xaramonitor_pop(h: *mut Handle, buf: *mut c_char, cap: usize) -> c_int {
    if h.is_null() || buf.is_null() || cap == 0 {
        return 0;
    }
    let handle = unsafe { &mut *h };
    let Some(line) = handle.mon.pop() else {
        return 0;
    };
    let text = line.text;
    let mut n = text.len().min(cap - 1);
    while n > 0 && !text.is_char_boundary(n) {
        n -= 1;
    }
    unsafe {
        std::ptr::copy_nonoverlapping(text.as_ptr(), buf.cast(), n);
        *buf.add(n) = 0;
    }
    1
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn xaramonitor_finish(h: *mut Handle) {
    if h.is_null() {
        return;
    }
    let handle = unsafe { &mut *h };
    if !handle.finished {
        handle.mon.finish();
        handle.finished = true;
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn xaramonitor_destroy(h: *mut Handle) {
    if !h.is_null() {
        unsafe {
            drop(Box::from_raw(h));
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn xaramonitor_version() -> *const c_char {
    concat!(env!("CARGO_PKG_VERSION"), "\0").as_ptr().cast()
}
