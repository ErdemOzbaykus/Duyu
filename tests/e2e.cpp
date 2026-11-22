// Real WASAPI end-to-end test, confined to its own child processes via onlyPid.
// Usage: e2e.exe "<part of an output device name>"   (plays a -60 dBFS tone there)
#include "../engine/wasapi.h"
#include "../engine/system.h"
#include "../engine/params.h"
#include <cstdio>
using namespace param;

#define FN(name, ret, ...) static ret (*name)(__VA_ARGS__)
FN(create, void*, const wchar_t*, uint32_t);
FN(destroy, void, void*);
FN(setParam, void, void*, int, int, float);
FN(setDevice, void, void*, int, const wchar_t*);
FN(assignApp, void, void*, const wchar_t*, int);
FN(meters, void, void*, float*, int);
FN(guard, void, const wchar_t*, uint32_t, uint32_t);

static int passed = 0, failed = 0;
static void check(bool ok, const char* name, double v = 0) {
    printf("%s  %-60s %12.6f\n", ok ? "PASS" : "FAIL", name, v);
    fflush(stdout);
    ok ? passed++ : failed++;
}
static std::wstring self() { wchar_t b[MAX_PATH]; GetModuleFileNameW(nullptr, b, MAX_PATH); return b; }
static PROCESS_INFORMATION spawn(const std::wstring& args) {
    std::wstring cmd = L"\"" + self() + L"\" " + args;
    STARTUPINFOW si{sizeof si};
    PROCESS_INFORMATION pi{};
    CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, TRUE, 0, nullptr, nullptr, &si, &pi);
    return pi;
}
static void finish(PROCESS_INFORMATION& p) {
    TerminateProcess(p.hProcess, 0);
    WaitForSingleObject(p.hProcess, 5000);
    CloseHandle(p.hProcess); CloseHandle(p.hThread);
}
static float volumeOf(DWORD pid) {
    for (auto& s : sys::listSessions()) if (s.pid == pid) return s.now[0];
    return -1;
}
static void load() {
    auto dir = self();
    dir.resize(dir.find_last_of(L'\\') + 1);
    HMODULE m = LoadLibraryW((dir + L"Duyu.Engine.dll").c_str());
    if (!m) { puts("engine DLL missing"); exit(2); }
#define GET(v, n) v = reinterpret_cast<decltype(v)>(GetProcAddress(m, n))
    GET(create, "duyu_create"); GET(destroy, "duyu_destroy"); GET(setParam, "duyu_set");
    GET(setDevice, "duyu_set_device"); GET(assignApp, "duyu_assign"); GET(meters, "duyu_meters"); GET(guard, "duyu_guard");
}

int wmain(int argc, wchar_t** argv) {
    if (argc < 2) return 2;
    wasapi::Com com;
    const std::wstring mode = argv[1];

    if (mode == L"tone") {
        wasapi::Stream s;
        double phase = 0;
        s.render(argv[2], [&](float* a, int n) {
            for (int i = 0; i < n; i++) {
                a[2 * i] = a[2 * i + 1] = float(.001 * std::sin(phase));
                phase = std::fmod(phase + 2 * 3.141592653589793 * 997 / 48000, 2 * 3.141592653589793);
            }
        });
        Sleep(60000);
        return 0;
    }
    load();
    if (mode == L"engine") { // a victim that gets killed
        void* e = create(argv[2], DWORD(_wtoi(argv[3])));
        setDevice(e, OutMain, argv[4]);
        setParam(e, MixerOn, 0, 1);
        Sleep(60000);
        return 0;
    }
    if (mode == L"guard") {
        guard(argv[2], DWORD(_wtoi(argv[3])), DWORD(_wtoi(argv[4])));
        return 0;
    }

    std::wstring id;
    for (auto& d : sys::listDevices()) if (!d.input && sys::has(d.name, argv[1])) id = d.id;
    if (id.empty()) { puts("output not found"); return 2; }
    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    const std::wstring dir = std::wstring(tmp) + L"DuyuE2E_" + std::to_wstring(GetCurrentProcessId());
    CreateDirectoryW(dir.c_str(), nullptr);
    const std::wstring toneArgs = L"tone \"" + id + L"\"";
    float m[METERS];

    auto app = spawn(toneArgs);
    Sleep(800);
    void* e = create(dir.c_str(), app.dwProcessId);
    setDevice(e, OutMain, id.c_str());
    assignApp(e, L"e2e.exe", Game);
    Sleep(700);
    meters(e, m, METERS);
    check(m[MMainRunning] == 0, "idle engine opens no output stream");
    check(m[MMicRunning] == 0, "idle engine opens no microphone");
    check(volumeOf(app.dwProcessId) == 1.f, "idle engine changes no volume", volumeOf(app.dwProcessId));

    setParam(e, MixerOn, 0, 1);
    Sleep(2000);
    meters(e, m, METERS);
    check(sys::isTamed(volumeOf(app.dwProcessId)), "original attenuated to -80 dB", volumeOf(app.dwProcessId));
    check(m[MCaptured] == 1, "app captured", m[MCaptured]);
    check(std::fabs(m[MOutL] / .001 - 1) < .01, "processed copy has unity gain", m[MOutL] / .001);
    check(std::fabs(m[MGameL] / .001 - 1) < .01, "audio reached the assigned channel", m[MGameL] / .001);

    float lowest = 1;
    for (int i = 0; i < 800; i++) { Sleep(10); meters(e, m, METERS); lowest = std::min(lowest, m[MOutL]); }
    check(lowest > .0009f, "8 s of captured audio without a single dropout", lowest / .001);

    setParam(e, Vol, Game, .5f);
    Sleep(800);
    meters(e, m, METERS);
    check(std::fabs(m[MOutL] / .0005 - 1) < .02, "fader works on real captured audio", m[MOutL] / .0005);
    setParam(e, Vol, Game, 1); setParam(e, SpatialOn, Game, 1); setParam(e, Immersion, Game, 0); // reflections comb a pure tone
    Sleep(800);
    meters(e, m, METERS);
    check(m[MOutL] > .0002 && std::fabs(m[MOutL] / m[MOutR] - 1) < .05, "spatial path runs, stereo stays balanced", m[MOutL] / m[MOutR]);
    setParam(e, SpatialOn, Game, 0);

    for (auto& s : sys::listSessions()) if (s.pid == app.dwProcessId) s.set(std::vector<float>(s.now.size(), .5f));
    float loudest = 0;
    for (int i = 0; i < 60; i++) { Sleep(20); meters(e, m, METERS); loudest = std::max(loudest, m[MOutL]); }
    check(loudest < .01, "external volume change cannot blast the ears", loudest);
    check(sys::isTamed(volumeOf(app.dwProcessId)), "attenuation re-applied", volumeOf(app.dwProcessId));

    setParam(e, MixerOn, 0, 0);
    Sleep(1000);
    meters(e, m, METERS);
    check(volumeOf(app.dwProcessId) == 1.f, "mixer off restores the app", volumeOf(app.dwProcessId));
    check(m[MPending] == 0, "journal empty after restore", m[MPending]);
    setParam(e, MixerOn, 0, 1);
    Sleep(1200);
    destroy(e);
    check(volumeOf(app.dwProcessId) == 1.f, "engine shutdown restores the app", volumeOf(app.dwProcessId));

    const std::wstring victimArgs = L"engine \"" + dir + L"\" " + std::to_wstring(app.dwProcessId) + L" \"" + id + L"\"";
    auto victim = spawn(victimArgs);
    Sleep(2000);
    check(sys::isTamed(volumeOf(app.dwProcessId)), "second engine attenuated the app", volumeOf(app.dwProcessId));
    auto dog = spawn(L"guard \"" + dir + L"\" " + std::to_wstring(victim.dwProcessId) + L" " + std::to_wstring(app.dwProcessId));
    Sleep(300);
    finish(victim);
    Sleep(2000);
    check(volumeOf(app.dwProcessId) == 1.f, "watchdog repairs after engine crash", volumeOf(app.dwProcessId));
    finish(dog);

    // The nasty case: the app exits while attenuated.
    e = create(dir.c_str(), app.dwProcessId);
    setDevice(e, OutMain, id.c_str());
    setParam(e, MixerOn, 0, 1);
    Sleep(1500);
    finish(app);
    Sleep(1000);
    destroy(e);
    app = spawn(toneArgs);
    Sleep(800);
    check(sys::isTamed(volumeOf(app.dwProcessId)), "Windows persisted attenuation (hazard reproduced)", volumeOf(app.dwProcessId));
    e = create(dir.c_str(), app.dwProcessId); // mixer stays off
    Sleep(1000);
    meters(e, m, METERS);
    check(volumeOf(app.dwProcessId) == 1.f, "returning app repaired with mixer off", volumeOf(app.dwProcessId));
    check(m[MPending] == 0, "journal cleared", m[MPending]);
    destroy(e);
    finish(app);
    app = spawn(toneArgs);
    Sleep(800);
    check(volumeOf(app.dwProcessId) == 1.f, "repair survives the next app launch", volumeOf(app.dwProcessId));
    finish(app);

    DeleteFileW((dir + L"\\restore.journal").c_str());
    RemoveDirectoryW(dir.c_str());
    printf("\n%d passed, %d failed\n", passed, failed);
    return failed ? 1 : 0;
}
