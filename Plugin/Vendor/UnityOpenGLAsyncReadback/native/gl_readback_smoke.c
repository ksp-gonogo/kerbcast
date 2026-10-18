/* Real readback through libAsyncGPUReadbackPlugin.so on a GL core context,
   loaded by absolute path the way the plugin does it, with UnityPluginLoad
   never called. Proves the library initialises GLEW on its own and returns the
   texture's exact bytes. Needs an X server (Xvfb is fine; Mesa llvmpipe works).

   Build: cc -o gl_readback_smoke gl_readback_smoke.c -lGL -lX11 -ldl
   Run:   gl_readback_smoke /abs/path/libAsyncGPUReadbackPlugin.so */

#include <GL/gl.h>
#include <GL/glx.h>
#include <X11/Xlib.h>
#include <dlfcn.h>
#include <stdio.h>
#include <string.h>

typedef GLXContext (*CreateContextAttribsFn)(Display *, GLXFBConfig, GLXContext, Bool, const int *);
typedef int (*RequestTextureFn)(GLuint, int);
typedef void (*RenderEventFn)(int);
typedef RenderEventFn (*GetFnPtrFn)(void);
typedef void (*VoidFn)(void);
typedef void (*GetDataFn)(int, void **, size_t *);
typedef _Bool (*TaskQueryFn)(int);
typedef int (*GlStateFn)(void);

#define W 4
#define H 2

static void *sym(void *lib, const char *name) {
    void *p = dlsym(lib, name);
    if (!p) { fprintf(stderr, "FAIL missing export %s\n", name); }
    return p;
}

int main(int argc, char **argv) {
    if (argc != 2) { fprintf(stderr, "usage: %s <lib path>\n", argv[0]); return 2; }

    Display *dpy = XOpenDisplay(NULL);
    if (!dpy) { fprintf(stderr, "FAIL no X display\n"); return 1; }
    static const int fb_attrs[] = { GLX_DRAWABLE_TYPE, GLX_PBUFFER_BIT, GLX_RENDER_TYPE, GLX_RGBA_BIT, None };
    int n = 0;
    GLXFBConfig *cfgs = glXChooseFBConfig(dpy, DefaultScreen(dpy), fb_attrs, &n);
    if (!cfgs || n == 0) { fprintf(stderr, "FAIL no fbconfig\n"); return 1; }
    CreateContextAttribsFn create = (CreateContextAttribsFn)glXGetProcAddressARB((const GLubyte *)"glXCreateContextAttribsARB");
    static const int ctx_attrs[] = {
        GLX_CONTEXT_MAJOR_VERSION_ARB, 4, GLX_CONTEXT_MINOR_VERSION_ARB, 1,
        GLX_CONTEXT_PROFILE_MASK_ARB, GLX_CONTEXT_CORE_PROFILE_BIT_ARB, None };
    GLXContext ctx = create(dpy, cfgs[0], NULL, True, ctx_attrs);
    static const int pb_attrs[] = { GLX_PBUFFER_WIDTH, 16, GLX_PBUFFER_HEIGHT, 16, None };
    GLXPbuffer pb = glXCreatePbuffer(dpy, cfgs[0], pb_attrs);
    if (!ctx || !glXMakeContextCurrent(dpy, pb, pb, ctx)) { fprintf(stderr, "FAIL no GL core context\n"); return 1; }
    printf("GL: %s | %s\n", glGetString(GL_VERSION), glGetString(GL_RENDERER));

    unsigned char pixels[W * H * 4];
    for (int i = 0; i < W * H * 4; i++) pixels[i] = (unsigned char)(i * 7 + 3);
    GLuint tex;
    glGenTextures(1, &tex);
    glBindTexture(GL_TEXTURE_2D, tex);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, W, H, 0, GL_RGBA, GL_UNSIGNED_BYTE, pixels);
    glBindTexture(GL_TEXTURE_2D, 0);

    void *lib = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!lib) { fprintf(stderr, "FAIL dlopen: %s\n", dlerror()); return 1; }
    RequestTextureFn request = (RequestTextureFn)sym(lib, "RequestTextureMainThread");
    GetFnPtrFn get_kick = (GetFnPtrFn)sym(lib, "GetKickstartFunctionPtr");
    GetFnPtrFn get_update = (GetFnPtrFn)sym(lib, "GetUpdateRenderThreadFunctionPtr");
    VoidFn update_main = (VoidFn)sym(lib, "UpdateMainThread");
    GetDataFn get_data = (GetDataFn)sym(lib, "GetData");
    TaskQueryFn done = (TaskQueryFn)sym(lib, "TaskDone");
    TaskQueryFn error = (TaskQueryFn)sym(lib, "TaskError");
    GlStateFn gl_state = (GlStateFn)sym(lib, "KerbcastGlState");
    if (!request || !get_kick || !get_update || !update_main || !get_data || !done || !error || !gl_state) return 1;

    int id = request(tex, 0);
    get_kick()(id);
    printf("KerbcastGlState after first render-thread call: %d\n", gl_state());
    if (gl_state() != 1) { fprintf(stderr, "FAIL GL not initialised\n"); return 1; }
    for (int i = 0; i < 1000 && !done(id); i++) {
        glFinish();
        get_update()(0);
        update_main();
    }
    if (!done(id) || error(id)) { fprintf(stderr, "FAIL task done=%d error=%d\n", done(id), error(id)); return 1; }

    void *buf = NULL;
    size_t len = 0;
    get_data(id, &buf, &len);
    if (!buf || len != sizeof pixels || memcmp(buf, pixels, sizeof pixels) != 0) {
        fprintf(stderr, "FAIL readback mismatch (len=%zu)\n", len);
        return 1;
    }
    printf("PASS read back %zu bytes identical to the uploaded texture\n", len);
    return 0;
}
