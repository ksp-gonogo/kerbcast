/*
 * ffi_shim.c — C-side accessors for FFmpeg struct fields whose offsets
 * may disagree between the Rust bindgen-generated layout and the compiled
 * runtime library.
 *
 * The root cause: bindgen generates AVCodecContext field offsets based on
 * which FF_API_* deprecation guards are active at bind-time. The compiled
 * libavcodec.so may have been built with a different set of guards, shifting
 * the offsets of late-added fields like hw_frames_ctx / hw_device_ctx.
 *
 * By writing these accessors in C and compiling them against the same
 * libavcodec headers used at Rust build time (same apt-installed package),
 * we guarantee that reads and writes land at the offset the library expects.
 */

#include <libavcodec/avcodec.h>
#include <libavutil/buffer.h>

void kerbcast_avcodec_set_hw_frames_ctx(AVCodecContext *ctx, AVBufferRef *ref) {
    ctx->hw_frames_ctx = ref;
}

AVBufferRef *kerbcast_avcodec_get_hw_frames_ctx(const AVCodecContext *ctx) {
    return ctx->hw_frames_ctx;
}

void kerbcast_avcodec_set_hw_device_ctx(AVCodecContext *ctx, AVBufferRef *ref) {
    ctx->hw_device_ctx = ref;
}
