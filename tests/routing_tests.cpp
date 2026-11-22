// sys::route on made-up device lists. Opens no device.
#include <windows.h>
#include <initguid.h>
#include "../engine/system.h"
#include <cstdio>
using namespace param;

static int passed = 0, failed = 0;

static sys::Device dev(const wchar_t* id, const wchar_t* name, bool input, bool isDefault, int form, bool isVirtual = false) {
    sys::Device d;
    d.id = id; d.name = name; d.input = input; d.isDefault = isDefault; d.form = form; d.isVirtual = isVirtual;
    return d;
}

static void check(const std::wstring& got, const wchar_t* want, const char* name) {
    const bool ok = got == want;
    printf("%s  %s\n", ok ? "PASS" : "FAIL", name);
    ok ? passed++ : failed++;
}

int main() {
    // As Windows looks right after installing VB-CABLE: the cable is default in both directions.
    const std::vector<sys::Device> all = {
        dev(L"cableIn", L"CABLE Input (VB-Audio Virtual Cable)", false, true, Speakers, true),
        dev(L"cable16", L"CABLE In 16ch (VB-Audio Virtual Cable)", false, false, Speakers, true),
        dev(L"monitor", L"Monitor (HDMI)", false, false, 9),
        dev(L"headset", L"Hoparlör (USB Headset)", false, false, Speakers),
        dev(L"cableOut", L"CABLE Output (VB-Audio Virtual Cable)", true, true, Microphone, true),
        dev(L"mic", L"Mikrofon (USB Headset)", true, false, Microphone),
    };
    {   std::wstring w[ROLES];
        sys::route(all, w, false);
        check(w[OutMain], L"headset", "auto: headphones, although the cable is default");
        check(w[InMic], L"mic", "auto: real microphone, although the cable is default");
        check(w[OutMic], L"cableIn", "auto: processed mic goes into the cable, not the 16ch one"); }
    {   std::wstring w[ROLES];
        w[OutMain] = L"cableIn"; w[InMic] = L"cableOut"; w[OutGame] = L"cableIn"; w[OutChat] = L"cable16";
        sys::route(all, w, false);
        check(w[OutMain], L"headset", "saved cable as headphones is ignored");
        check(w[InMic], L"mic", "saved cable as mic input is ignored");
        check(w[OutMic], L"cableIn", "broken settings still reach the cable");
        check(w[OutGame], L"", "game channel cannot leak into the cable");
        check(w[OutChat], L"", "chat channel cannot leak into the cable"); }
    {   std::wstring w[ROLES];
        w[OutMain] = L"monitor"; w[OutMic] = L"cable16"; w[OutMedia] = L"monitor";
        sys::route(all, w, false);
        check(w[OutMain], L"monitor", "explicit output respected");
        check(w[OutMic], L"cable16", "explicit mic output respected");
        check(w[OutMedia], L"monitor", "explicit channel output respected"); }
    {   std::wstring w[ROLES];
        w[OutStream] = L"cableIn";
        sys::route(all, w, true);
        check(w[OutStream], L"cableIn", "stream mix may use the cable");
        check(w[OutMic], L"", "mic is not sent twice next to the stream mix"); }
    {   std::wstring w[ROLES];
        w[OutMain] = L"unplugged"; w[InMic] = L"headset"; // gone, and wrong direction
        sys::route(all, w, false);
        check(w[OutMain], L"headset", "unplugged device falls back");
        check(w[InMic], L"mic", "an output cannot be a microphone"); }
    {   std::wstring w[ROLES];
        sys::route({all[0], all[1], all[4]}, w, false);
        check(w[OutMain], L"", "only a cable: no headphones rather than a leak");
        check(w[InMic], L"", "only a cable: no microphone rather than a loop"); }
    {   std::wstring w[ROLES];
        sys::route({all[3], all[5]}, w, false);
        check(w[OutMain], L"headset", "no cable: output unchanged");
        check(w[OutMic], L"", "no cable: mic is published nowhere"); }

    printf("\nrouting: %d passed, %d failed\n", passed, failed);
    return failed ? 1 : 0;
}
