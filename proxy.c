/*
 * kazumi-mpv-proxy (instrumented build)
 * Proxy for libmpv-2.dll with logging to D:\KazumiHelper\proxy_log.txt
 */
#include <windows.h>
#include <stdio.h>
#include <stdarg.h>

#define PIPE_NAME "\\\\.\\pipe\\kazumi-mpv-ipc"
#define PROXY_VERSION "1.0.1"

/* 日志写到代理 DLL 自己旁边（即 Kazumi 目录），避免写死路径在别人机器上失效 */
static wchar_t g_log_path[32768] = L"";

static void init_log_path(HINSTANCE hinst)
{
    wchar_t path[32768];
    DWORD n = GetModuleFileNameW(hinst, path, 32768);
    wchar_t *slash;
    if (!n || n >= 32768) return;
    slash = wcsrchr(path, L'\\');
    if (!slash) return;
    lstrcpynW(slash + 1, L"proxy_log.txt", 32768 - (int)(slash + 1 - path));
    lstrcpynW(g_log_path, path, 32768);
}

static void proxy_log(const char *fmt, ...);

typedef int (__cdecl *pfn_mpv_set_option_string)(void *, const char *, const char *);
typedef int (__cdecl *pfn_mpv_initialize)(void *);
typedef void * (__cdecl *pfn_mpv_create)(void);
typedef unsigned long (__cdecl *pfn_mpv_client_api_version)(void);

static HMODULE get_real_module(void)
{
    HMODULE real = GetModuleHandleA("libmpv-real.dll");
    if (!real)
        real = LoadLibraryA("libmpv-real.dll");
    return real;
}

/*
 * 关键拦截：media_kit fork 用 native assets 解析 mpv_client_api_version，
 * 再把它“所在的模块”当作 libmpv 路径，之后所有函数直接绑到那个模块。
 * 让这个函数返回代理自己的地址，反查就会定位到本代理，
 * 后续 mpv_initialize 拦截才能生效。
 */
__declspec(dllexport) unsigned long __cdecl mpv_client_api_version(void)
{
    HMODULE real = get_real_module();
    pfn_mpv_client_api_version f;
    if (!real)
    {
        proxy_log("client_api_version: real module missing");
        return 3002; /* MPV_CLIENT_API_VERSION 3.2 兜底 */
    }
    f = (pfn_mpv_client_api_version)GetProcAddress(real, "mpv_client_api_version");
    proxy_log("mpv_client_api_version -> forwarded, addr=%p", (void *)f);
    return f ? f() : 3002;
}

static void proxy_log(const char *fmt, ...)
{
    FILE *f;
    if (!g_log_path[0]) return;
    f = _wfopen(g_log_path, L"a");
    if (!f) return;
    {
        SYSTEMTIME st;
        GetLocalTime(&st);
        fprintf(f, "[%02d:%02d:%02d.%03d pid=%lu] ",
                st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                (unsigned long)GetCurrentProcessId());
    }
    {
        va_list ap;
        va_start(ap, fmt);
        vfprintf(f, fmt, ap);
        va_end(ap);
    }
    fputc('\n', f);
    fclose(f);
}

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(hinst);
        init_log_path(hinst);
        proxy_log("proxy v%s loaded (process attach)", PROXY_VERSION);
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        proxy_log("proxy detach");
    }
    return TRUE;
}

__declspec(dllexport) int __cdecl mpv_initialize(void *ctx)
{
    HMODULE real;
    pfn_mpv_set_option_string set_opt;
    pfn_mpv_initialize init;
    int rc_opt, rc_init;

    proxy_log("mpv_initialize entered, ctx=%p", ctx);

    real = get_real_module();
    if (!real)
    {
        proxy_log("FAILED to locate libmpv-real.dll");
        return -3;
    }
    proxy_log("real module=%p", (void *)real);

    set_opt = (pfn_mpv_set_option_string)GetProcAddress(real, "mpv_set_option_string");
    init = (pfn_mpv_initialize)GetProcAddress(real, "mpv_initialize");
    if (!init)
    {
        proxy_log("GetProcAddress(mpv_initialize) failed");
        return -3;
    }

    rc_opt = set_opt ? set_opt(ctx, "input-ipc-server", PIPE_NAME) : -999;
    proxy_log("set_opt(input-ipc-server) rc=%d", rc_opt);

    rc_init = init(ctx);
    proxy_log("real mpv_initialize rc=%d", rc_init);
    return rc_init;
}

/* ---- export forwarders (auto-generated from dumpbin /exports) ---- */
#pragma comment(linker, "/export:mpv_abort_async_command=libmpv-real.mpv_abort_async_command")
#pragma comment(linker, "/export:mpv_client_id=libmpv-real.mpv_client_id")
#pragma comment(linker, "/export:mpv_client_name=libmpv-real.mpv_client_name")
#pragma comment(linker, "/export:mpv_command=libmpv-real.mpv_command")
#pragma comment(linker, "/export:mpv_command_async=libmpv-real.mpv_command_async")
#pragma comment(linker, "/export:mpv_command_node=libmpv-real.mpv_command_node")
#pragma comment(linker, "/export:mpv_command_node_async=libmpv-real.mpv_command_node_async")
#pragma comment(linker, "/export:mpv_command_ret=libmpv-real.mpv_command_ret")
#pragma comment(linker, "/export:mpv_command_string=libmpv-real.mpv_command_string")
#pragma comment(linker, "/export:mpv_create=libmpv-real.mpv_create")
#pragma comment(linker, "/export:mpv_create_client=libmpv-real.mpv_create_client")
#pragma comment(linker, "/export:mpv_create_weak_client=libmpv-real.mpv_create_weak_client")
#pragma comment(linker, "/export:mpv_del_property=libmpv-real.mpv_del_property")
#pragma comment(linker, "/export:mpv_destroy=libmpv-real.mpv_destroy")
#pragma comment(linker, "/export:mpv_error_string=libmpv-real.mpv_error_string")
#pragma comment(linker, "/export:mpv_event_name=libmpv-real.mpv_event_name")
#pragma comment(linker, "/export:mpv_event_to_node=libmpv-real.mpv_event_to_node")
#pragma comment(linker, "/export:mpv_free=libmpv-real.mpv_free")
#pragma comment(linker, "/export:mpv_free_node_contents=libmpv-real.mpv_free_node_contents")
#pragma comment(linker, "/export:mpv_get_property=libmpv-real.mpv_get_property")
#pragma comment(linker, "/export:mpv_get_property_async=libmpv-real.mpv_get_property_async")
#pragma comment(linker, "/export:mpv_get_property_osd_string=libmpv-real.mpv_get_property_osd_string")
#pragma comment(linker, "/export:mpv_get_property_string=libmpv-real.mpv_get_property_string")
#pragma comment(linker, "/export:mpv_get_time_ns=libmpv-real.mpv_get_time_ns")
#pragma comment(linker, "/export:mpv_get_time_us=libmpv-real.mpv_get_time_us")
#pragma comment(linker, "/export:mpv_get_wakeup_pipe=libmpv-real.mpv_get_wakeup_pipe")
#pragma comment(linker, "/export:mpv_hook_add=libmpv-real.mpv_hook_add")
#pragma comment(linker, "/export:mpv_hook_continue=libmpv-real.mpv_hook_continue")
#pragma comment(linker, "/export:mpv_load_config_file=libmpv-real.mpv_load_config_file")
#pragma comment(linker, "/export:mpv_observe_property=libmpv-real.mpv_observe_property")
#pragma comment(linker, "/export:mpv_render_context_create=libmpv-real.mpv_render_context_create")
#pragma comment(linker, "/export:mpv_render_context_free=libmpv-real.mpv_render_context_free")
#pragma comment(linker, "/export:mpv_render_context_get_info=libmpv-real.mpv_render_context_get_info")
#pragma comment(linker, "/export:mpv_render_context_render=libmpv-real.mpv_render_context_render")
#pragma comment(linker, "/export:mpv_render_context_report_swap=libmpv-real.mpv_render_context_report_swap")
#pragma comment(linker, "/export:mpv_render_context_set_parameter=libmpv-real.mpv_render_context_set_parameter")
#pragma comment(linker, "/export:mpv_render_context_set_update_callback=libmpv-real.mpv_render_context_set_update_callback")
#pragma comment(linker, "/export:mpv_render_context_update=libmpv-real.mpv_render_context_update")
#pragma comment(linker, "/export:mpv_request_event=libmpv-real.mpv_request_event")
#pragma comment(linker, "/export:mpv_request_log_messages=libmpv-real.mpv_request_log_messages")
#pragma comment(linker, "/export:mpv_set_option=libmpv-real.mpv_set_option")
#pragma comment(linker, "/export:mpv_set_option_string=libmpv-real.mpv_set_option_string")
#pragma comment(linker, "/export:mpv_set_property=libmpv-real.mpv_set_property")
#pragma comment(linker, "/export:mpv_set_property_async=libmpv-real.mpv_set_property_async")
#pragma comment(linker, "/export:mpv_set_property_string=libmpv-real.mpv_set_property_string")
#pragma comment(linker, "/export:mpv_set_wakeup_callback=libmpv-real.mpv_set_wakeup_callback")
#pragma comment(linker, "/export:mpv_stream_cb_add_ro=libmpv-real.mpv_stream_cb_add_ro")
#pragma comment(linker, "/export:mpv_terminate_destroy=libmpv-real.mpv_terminate_destroy")
#pragma comment(linker, "/export:mpv_unobserve_property=libmpv-real.mpv_unobserve_property")
#pragma comment(linker, "/export:mpv_wait_async_requests=libmpv-real.mpv_wait_async_requests")
#pragma comment(linker, "/export:mpv_wait_event=libmpv-real.mpv_wait_event")
#pragma comment(linker, "/export:mpv_wakeup=libmpv-real.mpv_wakeup")
