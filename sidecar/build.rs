fn main() {
    // The C shim is only needed on Linux where the VAAPI backend is active.
    // macOS dev builds skip this; libavcodec-dev is not expected there.
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("linux") {
        build_ffi_shim();
    }
}

fn build_ffi_shim() {
    let lib = pkg_config::probe_library("libavcodec")
        .expect("libavcodec not found; install libavcodec-dev");

    let mut build = cc::Build::new();
    build.file("src/encoder/ffi_shim.c");
    for path in &lib.include_paths {
        build.include(path);
    }
    build.compile("kerbcast_ffi_shim");

    println!("cargo:rerun-if-changed=src/encoder/ffi_shim.c");
}
