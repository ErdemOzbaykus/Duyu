// Measures the delay the engine adds to app audio, with its own child process as the app.
// Child render callback -> engine main-output callback. The child's own output buffer is what
// an app has without Duyu too; the engine's output buffer is added on top and reported.
#include "../engine/wasapi.h"
#include "../engine/system.h"
#include "../engine/params.h"
#include <cstdio>
using namespace param;

static LONGLONG qpc() { LARGE_INTEGER t; QueryPerformanceCounter(&t); return t.QuadPart; }

int wmain(int argc, wchar_t** argv) {
    if (argc < 2) return 2;
    wasapi::Com com;
    HANDLE map = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, 16, L"Local\\DuyuLatencyTest");
    auto* shared = static_cast<volatile LONGLONG*>(MapViewOfFile(map, FILE_MAP_WRITE, 0, 0, 16));

    if (argc > 2) { // the app: silence, then a tone whenever asked
        wasapi::Stream s;
        bool sounding = false;
        double ph = 0;
        s.render(argv[2], [&](float* a, int n) {
            const bool want = shared[0] != 0;
            if (want && !sounding) shared[1] = qpc();
            sounding = want;
            for (int i = 0; i < n; i++) {
                a[2 * i] = a[2 * i + 1] = want ? float(.001 * std::cos(ph)) : 0.f;
                ph = std::fmod(ph + .13, 6.283185307179586);
            }
        });
        Sleep(30000);
        return 0;
    }

    std::wstring id;
    for (auto& d : sys::listDevices()) if (!d.input && sys::has(d.name, argv[1])) id = d.id;
    if (id.empty()) return 2;
    wchar_t exe[MAX_PATH], tmp[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    GetTempPathW(MAX_PATH, tmp);
    shared[0] = 0;
    std::wstring cmd = L"\"" + std::wstring(exe) + L"\" child \"" + id + L"\"";
    STARTUPINFOW si{sizeof si};
    PROCESS_INFORMATION pi{};
    CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi);
    Sleep(700);

    std::wstring dll(exe);
    dll.resize(dll.find_last_of(L'\\') + 1);
    HMODULE m = LoadLibraryW((dll + L"Duyu.Engine.dll").c_str());
    auto create = (void* (*)(const wchar_t*, uint32_t))GetProcAddress(m, "duyu_create");
    auto destroy = (void (*)(void*))GetProcAddress(m, "duyu_destroy");
    auto set = (void (*)(void*, int, int, float))GetProcAddress(m, "duyu_set");
    auto device = (void (*)(void*, int, const wchar_t*))GetProcAddress(m, "duyu_set_device");
    auto meters = (void (*)(void*, float*, int))GetProcAddress(m, "duyu_meters");

    const std::wstring dir = std::wstring(tmp) + L"DuyuLatency_" + std::to_wstring(GetCurrentProcessId());
    CreateDirectoryW(dir.c_str(), nullptr);
    void* e = create(dir.c_str(), pi.dwProcessId);
    device(e, OutMain, id.c_str());
    set(e, MixerOn, 0, 1);
    Sleep(1500);

    LARGE_INTEGER hz;
    QueryPerformanceFrequency(&hz);
    std::vector<double> ms;
    float v[METERS];
    for (int round = 0; round < 12; round++) {
        shared[0] = 0;
        shared[1] = 0;
        Sleep(700 + round * 7);
        shared[0] = 1;
        const auto started = GetTickCount64();
        while (GetTickCount64() - started < 1000) {
            meters(e, v, METERS);
            if (shared[1] && v[MOutL] > .0003f) { ms.push_back((qpc() - shared[1]) * 1000. / hz.QuadPart); break; }
            YieldProcessor();
        }
    }
    destroy(e);
    DeleteFileW((dir + L"\\restore.journal").c_str());
    RemoveDirectoryW(dir.c_str());
    TerminateProcess(pi.hProcess, 0);
    std::sort(ms.begin(), ms.end());
    if (ms.empty()) { puts("no signal"); return 1; }
    printf("app write -> engine output callback: median %.1f ms (min %.1f, max %.1f, n=%zu)\n", ms[ms.size() / 2], ms.front(), ms.back(), ms.size());
    puts("plus engine output buffer: 15 ms on a 10 ms endpoint");
    return 0;
}
