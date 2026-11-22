// Real devices: the delay of the sidetone path inside the PC, first captured sample -> endpoint.
// Plays silence and keeps no audio. Radio and converters inside the headset are not included.
//   sidetone_latency.exe <part of the device name> [seconds]
#include "../engine/wasapi.h"
#include "../engine/system.h"
#include <cstdio>

int wmain(int argc, wchar_t** argv) {
    if (argc < 2) return 2;
    wasapi::Com com;
    const auto part = sys::lower(argv[1]);
    std::wstring in, out;
    for (auto& d : sys::listDevices())
        if (sys::has(sys::lower(d.name), part.c_str())) (d.input ? in : out) = d.id;
    if (in.empty() || out.empty()) { puts("device not found"); return 2; }

    const int seconds = argc > 2 ? std::max(1, _wtoi(argv[2])) : 30;
    wasapi::Stream s;
    s.monitor(in, out, [](float* a, int n) { std::memset(a, 0, sizeof(float) * 2 * n); });
    Sleep(3000); // start-up and the first trim
    const int gaps = s.gaps;
    std::vector<float> ms;
    for (int i = 0; i < seconds * 20; i++) {
        Sleep(50);
        if (s.phase != wasapi::Stream::Running) { puts("stream failed"); return 1; }
        ms.push_back(s.delayMs);
    }
    s.stop();
    std::sort(ms.begin(), ms.end());
    printf("sidetone in PC: median %.1f ms (p5 %.1f, p95 %.1f, max %.1f) | gaps %d | trims %d | %d s\n",
           ms[ms.size() / 2], ms[ms.size() / 20], ms[ms.size() * 19 / 20], ms.back(), s.gaps - gaps, s.trims.load(), seconds);
}
