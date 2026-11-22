// Devices, processes, audio sessions and the crash-recovery journal.
#pragma once
#include "wasapi.h"
#include "params.h"
#include <tlhelp32.h>
#include <algorithm>
#include <cwctype>
#include <map>
#include <set>

namespace sys {
using wasapi::ComPtr;

// Per-channel session volume while Duyu owns an app (-80 dB). The slider in the Windows
// mixer is the session *master* volume and is never touched.
constexpr float TAME = 1e-4f;
inline bool isTamed(float v) { return v > TAME * .98f && v < TAME * 1.02f; }

inline std::wstring lower(std::wstring s) {
    for (auto& c : s) c = wchar_t(std::towlower(c));
    return s;
}
inline bool has(const std::wstring& s, const wchar_t* part) { return s.find(part) != std::wstring::npos; }

struct Device {
    std::wstring id, name;
    bool input = false, isDefault = false, isVirtual = false;
    int form = 10; // EndpointFormFactor
    int rate = 0;
};

inline ComPtr<IMMDeviceEnumerator> enumerator() {
    ComPtr<IMMDeviceEnumerator> e;
    CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&e));
    return e;
}

inline std::vector<Device> listDevices() {
    std::vector<Device> out;
    auto en = enumerator();
    if (!en) return out;
    for (EDataFlow flow : {eRender, eCapture}) {
        std::wstring def;
        ComPtr<IMMDevice> d;
        LPWSTR s = nullptr;
        if (SUCCEEDED(en->GetDefaultAudioEndpoint(flow, eConsole, &d)) && SUCCEEDED(d->GetId(&s))) { def = s; CoTaskMemFree(s); }
        ComPtr<IMMDeviceCollection> col;
        UINT count = 0;
        if (FAILED(en->EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, &col))) continue;
        col->GetCount(&count);
        for (UINT i = 0; i < count; i++) {
            ComPtr<IMMDevice> dev;
            if (FAILED(col->Item(i, &dev)) || FAILED(dev->GetId(&s))) continue;
            Device info;
            info.id = s; CoTaskMemFree(s);
            info.input = flow == eCapture;
            info.isDefault = info.id == def;
            ComPtr<IPropertyStore> props;
            if (SUCCEEDED(dev->OpenPropertyStore(STGM_READ, &props))) {
                PROPVARIANT v;
                PropVariantInit(&v);
                if (SUCCEEDED(props->GetValue(PKEY_Device_FriendlyName, &v)) && v.vt == VT_LPWSTR) info.name = v.pwszVal;
                PropVariantClear(&v);
                if (SUCCEEDED(props->GetValue(PKEY_AudioEndpoint_FormFactor, &v)) && v.vt == VT_UI4) info.form = int(v.ulVal);
                PropVariantClear(&v);
                if (SUCCEEDED(props->GetValue(PKEY_AudioEngine_DeviceFormat, &v)) && v.vt == VT_BLOB && v.blob.cbSize >= sizeof(WAVEFORMATEX) - 2)
                    info.rate = int(reinterpret_cast<WAVEFORMATEX*>(v.blob.pBlobData)->nSamplesPerSec);
                PropVariantClear(&v);
            }
            for (auto& c : info.name) if (c == L'\t' || c == L'\n') c = L' ';
            const auto n = lower(info.name);
            for (auto* word : {L"virtual", L"sonar", L"cable", L"voicemeeter", L"steam streaming", L"broadcast"})
                info.isVirtual |= has(n, word);
            out.push_back(std::move(info));
        }
    }
    return out;
}

// shortcut: only VB-Audio cables are recognised, add names here when another cable driver needs auto-routing.
inline bool isCable(const Device& d) {
    const auto n = lower(d.name);
    return has(n, L"vb-audio") && has(n, L"cable");
}

inline std::wstring pickOutput(const std::vector<Device>& ds) {
    auto score = [](const Device& d) {
        int s = d.isDefault ? 50 : 0;
        if (d.isVirtual) s -= 100;
        if (d.form == Headphones || d.form == Headset) s += 30;
        else if (d.form == Speakers) s += 20;
        return s;
    };
    const Device* best = nullptr;
    for (auto& d : ds) if (!d.input && !isCable(d) && (!best || score(d) > score(*best))) best = &d;
    return best ? best->id : L"";
}

inline std::wstring pickInput(const std::vector<Device>& ds) {
    const Device* best = nullptr;
    auto score = [](const Device& d) { return (d.isDefault ? 50 : 0) - (d.isVirtual ? 100 : 0); };
    for (auto& d : ds) if (d.input && !isCable(d) && (!best || score(d) > score(*best))) best = &d;
    return best ? best->id : L"";
}

// Turns saved choices into the devices to open. A cable is the virtual microphone, so it may only
// carry the processed mic or the stream mix: never the headphones, a channel or the mic input.
inline void route(const std::vector<Device>& ds, std::wstring (&want)[param::ROLES], bool streamer) {
    using namespace param;
    for (int r = 0; r < ROLES; r++) {
        const bool cableAllowed = r == OutMic || r == OutStream;
        const bool usable = std::any_of(ds.begin(), ds.end(), [&](auto& d) {
            return d.id == want[r] && d.input == (r >= InMic) && (cableAllowed || !isCable(d));
        });
        if (!usable) want[r].clear();
    }
    if (want[OutMain].empty()) want[OutMain] = pickOutput(ds);
    if (want[InMic].empty()) want[InMic] = pickInput(ds);
    if (want[OutMic].empty())
        for (auto& d : ds)
            if (!d.input && isCable(d) && has(lower(d.name), L"input")) { want[OutMic] = d.id; break; }
    if (streamer && want[OutMic] == want[OutStream]) want[OutMic].clear(); // the stream mix already carries the mic
}

struct Processes {
    struct Info { DWORD parent; std::wstring exe; };
    std::map<DWORD, Info> all;
    mutable std::map<DWORD, ULONGLONG> born;

    Processes() {
        HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE) return;
        PROCESSENTRY32W e{sizeof e};
        if (Process32FirstW(snap, &e))
            do all[e.th32ProcessID] = {e.th32ParentProcessID, lower(e.szExeFile)}; while (Process32NextW(snap, &e));
        CloseHandle(snap);
    }
    ULONGLONG birth(DWORD pid) const {
        if (auto it = born.find(pid); it != born.end()) return it->second;
        ULONGLONG t = 0;
        if (HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)) {
            FILETIME c, x, k, u;
            if (GetProcessTimes(h, &c, &x, &k, &u)) t = (ULONGLONG(c.dwHighDateTime) << 32) | c.dwLowDateTime;
            CloseHandle(h);
        }
        return born[pid] = t;
    }
    std::vector<DWORD> ancestors(DWORD pid) const {
        std::vector<DWORD> out;
        auto it = all.find(pid);
        while (it != all.end() && out.size() < 24) {
            const DWORD parent = it->second.parent;
            if (!parent || parent == pid || !all.count(parent)) break;
            const ULONGLONG tp = birth(parent), tc = birth(pid);
            if (tp && tc && tp > tc) break; // the parent id was reused by a newer process
            out.push_back(parent);
            pid = parent;
            it = all.find(pid);
        }
        return out;
    }
    std::wstring exe(DWORD pid) const {
        auto it = all.find(pid);
        return it == all.end() ? L"" : it->second.exe;
    }
    static std::wstring path(DWORD pid) {
        wchar_t buf[1024];
        DWORD n = 1024;
        std::wstring out;
        if (HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)) {
            if (QueryFullProcessImageNameW(h, 0, buf, &n)) out.assign(buf, n);
            CloseHandle(h);
        }
        return lower(out);
    }
};

struct Session {
    DWORD pid = 0;
    std::wstring id;
    bool active = false;
    ComPtr<IChannelAudioVolume> volume;
    std::vector<float> now;

    bool tamed() const { return !now.empty() && isTamed(now[0]); }
    bool set(const std::vector<float>& v) {
        if (v.size() != now.size() || FAILED(volume->SetAllVolumes(UINT32(v.size()), v.data(), nullptr))) return false;
        now = v;
        return true;
    }
};

inline std::vector<Session> listSessions() {
    std::vector<Session> out;
    auto en = enumerator();
    ComPtr<IMMDeviceCollection> col;
    if (!en || FAILED(en->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &col))) return out;
    UINT devices = 0;
    col->GetCount(&devices);
    for (UINT i = 0; i < devices; i++) {
        ComPtr<IMMDevice> dev;
        ComPtr<IAudioSessionManager2> mgr;
        ComPtr<IAudioSessionEnumerator> list;
        if (FAILED(col->Item(i, &dev)) || FAILED(dev->Activate(__uuidof(IAudioSessionManager2), CLSCTX_ALL, nullptr, &mgr))) continue;
        if (FAILED(mgr->GetSessionEnumerator(&list))) continue;
        int count = 0;
        list->GetCount(&count);
        for (int k = 0; k < count; k++) {
            ComPtr<IAudioSessionControl> ctl;
            ComPtr<IAudioSessionControl2> c2;
            if (FAILED(list->GetSession(k, &ctl)) || FAILED(ctl.As(&c2))) continue;
            if (c2->IsSystemSoundsSession() == S_OK) continue;
            AudioSessionState st;
            if (FAILED(ctl->GetState(&st)) || st == AudioSessionStateExpired) continue;
            Session s;
            s.active = st == AudioSessionStateActive;
            if (FAILED(c2->GetProcessId(&s.pid)) || !s.pid || FAILED(ctl.As(&s.volume))) continue;
            LPWSTR id = nullptr;
            if (FAILED(c2->GetSessionIdentifier(&id))) continue;
            s.id = id; CoTaskMemFree(id);
            UINT32 n = 0;
            if (FAILED(s.volume->GetChannelCount(&n)) || !n || n > 64) continue;
            s.now.resize(n);
            if (FAILED(s.volume->GetAllVolumes(n, s.now.data()))) continue;
            out.push_back(std::move(s));
        }
    }
    return out;
}

// Written *before* a session is attenuated. Windows persists channel volume per app and
// ignores writes to expired sessions, so a closed app can only be repaired when it returns.
class Journal {
public:
    std::map<std::wstring, std::vector<float>> entries;

    explicit Journal(std::wstring file) : file_(std::move(file)) { load(); }

    void load() {
        entries.clear();
        HANDLE h = CreateFileW(file_.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
        if (h == INVALID_HANDLE_VALUE) return;
        std::wstring text(GetFileSize(h, nullptr) / sizeof(wchar_t), 0);
        DWORD got = 0;
        ReadFile(h, text.data(), DWORD(text.size() * sizeof(wchar_t)), &got, nullptr);
        CloseHandle(h);
        size_t from = 0, end;
        while ((end = text.find(L'\n', from)) != std::wstring::npos) {
            const auto line = text.substr(from, end - from);
            from = end + 1;
            const auto tab = line.find(L'\t');
            if (tab == std::wstring::npos) continue;
            auto& v = entries[line.substr(0, tab)];
            const wchar_t* p = line.c_str() + tab + 1;
            while (*p) {
                wchar_t* next;
                const float f = std::wcstof(p, &next);
                if (next == p) break;
                v.push_back(f);
                p = *next == L' ' ? next + 1 : next;
            }
        }
    }
    bool save() const {
        std::wstring text;
        for (auto& [id, v] : entries) {
            text += id + L'\t';
            for (float f : v) text += std::to_wstring(f) + L' ';
            text += L'\n';
        }
        const auto tmp = file_ + L".tmp";
        HANDLE h = CreateFileW(tmp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
        if (h == INVALID_HANDLE_VALUE) return false;
        DWORD wrote = 0;
        const bool ok = WriteFile(h, text.data(), DWORD(text.size() * sizeof(wchar_t)), &wrote, nullptr) && FlushFileBuffers(h);
        CloseHandle(h);
        return ok && MoveFileExW(tmp.c_str(), file_.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
    }
    bool remember(const Session& s) {
        if (entries.count(s.id)) return true;
        entries[s.id] = s.tamed() ? std::vector<float>(s.now.size(), 1.f) : s.now;
        if (save()) return true;
        entries.erase(s.id);
        return false;
    }
    // Undo attenuation on every listed session that `keep` does not claim.
    template <class Keep> void repair(std::vector<Session>& sessions, Keep keep) {
        std::set<std::wstring> fixed, held;
        for (auto& s : sessions) {
            if (keep(s)) { held.insert(s.id); continue; }
            auto it = entries.find(s.id);
            if (s.tamed()) {
                std::vector<float> original(s.now.size(), 1.f);
                if (it != entries.end() && it->second.size() == original.size()) original = it->second;
                if (!s.set(original)) { held.insert(s.id); continue; }
            }
            if (it != entries.end()) fixed.insert(s.id);
        }
        bool dirty = false;
        for (auto& id : fixed) if (!held.count(id)) dirty |= entries.erase(id) > 0;
        if (dirty) save();
    }
private:
    std::wstring file_;
};
} // namespace sys
