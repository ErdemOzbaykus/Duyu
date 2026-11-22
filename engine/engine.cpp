// Duyu.Engine.dll: owns the streams and exposes a small C ABI to the UI.
#include "wasapi.h"
#include "system.h"
#include "graph.h"
#include <memory>
#include <mutex>

namespace {
using namespace param;

constexpr int SOURCES = 32;

const wchar_t* const NEVER[] = {
    L"audiodg.exe", L"steelseriessonar.exe", L"nvidia broadcast.exe", L"voicemeeter.exe",
    L"voicemeeterpro.exe", L"voicemeeter8.exe", L"voicemeeter8x64.exe", L"explorer.exe", L"svchost.exe", L"duyu.exe"};

int classify(const std::wstring& exe, const std::wstring& path) {
    for (auto* s : {L"discord", L"teams", L"slack", L"teamspeak", L"ts3client", L"mumble", L"skype", L"zoom", L"telegram", L"whatsapp", L"element", L"guilded"})
        if (sys::has(exe, s)) return Chat;
    for (auto* s : {L"spotify", L"chrome", L"firefox", L"msedge", L"opera", L"brave", L"vivaldi", L"zen.exe", L"arc.exe", L"vlc", L"mpv", L"music", L"tidal", L"foobar", L"plex", L"media", L"aimp", L"itunes"})
        if (sys::has(exe, s)) return Media;
    for (auto* s : {L"\\steamapps\\common\\", L"\\epic games\\", L"\\riot games\\", L"\\xboxgames\\", L"\\ea games\\", L"\\gog galaxy\\games\\", L"\\ubisoft game launcher\\games\\", L"\\battle.net\\", L"-win64-shipping", L"\\games\\"})
        if (sys::has(path, s)) return Game;
    return Aux;
}

std::wstring mutexName(const std::wstring& dir) {
    return L"Local\\DuyuEngine_" + std::to_wstring(std::hash<std::wstring>{}(sys::lower(dir)));
}

enum AppState { Idle, Captured, Direct, Starting, SharedTree, Error };

struct Source {
    wasapi::Stream stream;
    dsp::Fifo fifo{dsp::SURROUND, 96, 2400};
    std::atomic<bool> live{false};
    std::atomic<int> channel{Aux};
    std::atomic<float> makeup{0};
    DWORD pid = 0;
    ULONGLONG born = 0, settled = 0;
    float gain = 0; // capture thread only
    int hold = 0;   // capture thread only
};

class Engine {
public:
    graph::State state;

    Engine(std::wstring dir, DWORD onlyPid)
        : onlyPid_(onlyPid), journal_(dir + L"\\restore.journal"), wake_(CreateEventW(nullptr, FALSE, FALSE, nullptr)) {
        mutex_ = CreateMutexW(nullptr, FALSE, mutexName(dir).c_str());
        for (auto& s : sources_) s = std::make_unique<Source>();
        control_ = std::thread([this] { control(); });
    }
    ~Engine() {
        quit_ = true;
        SetEvent(wake_);
        control_.join();
        CloseHandle(wake_);
        if (mutex_) CloseHandle(mutex_);
    }

    void set(int id, int i, float v) {
        if (id < 0 || id >= COUNT || i < 0 || i >= INDICES || !std::isfinite(v)) return;
        // A corrupt settings file must not become a loud noise.
        switch (id) {
        case MasterVol: case Vol: case StreamVol: case Sidetone: v = std::clamp(v, 0.f, 1.f); break;
        case Ceiling: v = std::clamp(v, -24.f, -.1f); break;
        case Boost: v = std::clamp(v, 0.f, 12.f); break;
        case Bass: case Voice: case Treble: v = std::clamp(v, -12.f, 12.f); break;
        case Preamp: v = std::clamp(v, -24.f, 12.f); break;
        case MicGain: v = std::clamp(v, -24.f, 24.f); break;
        case SmartTarget: v = std::clamp(v, -32.f, -10.f); break;
        case SmartAmount: case DenoiseAmount: case Immersion: case Distance: case NsAmount: case ImpactAmount:
        case CompAmount: v = std::clamp(v, 0.f, 1.f); break;
        case ChatMix: v = std::clamp(v, -1.f, 1.f); break;
        }
        state.set(id, i, v);
        if (id == MixerOn || id == MicOn || id == StreamerOn || id == SpatialTest) SetEvent(wake_);
    }
    void setBand(int ch, int i, float on, float type, float f, float gain, float q) {
        if (ch < 0 || ch >= CHANNELS || i < 0 || i >= dsp::Equalizer::N) return;
        const float v[5] = {on, std::clamp(type, 0.f, 5.f), std::clamp(f, 10.f, 22000.f), std::clamp(gain, -18.f, 18.f), std::clamp(q, .1f, 30.f)};
        for (int k = 0; k < 5; k++) if (std::isfinite(v[k])) state.band[ch][i][k] = v[k];
    }
    void setDevice(int role, const wchar_t* id) {
        if (role < 0 || role >= ROLES) return;
        { std::lock_guard lock(mu_); wanted_[role] = id ? id : L""; }
        SetEvent(wake_);
    }
    void assign(const wchar_t* exe, int ch) { // -1: leave to Windows, -2: automatic
        if (!exe) return;
        { std::lock_guard lock(mu_);
          if (ch < -1 || ch > Aux) assign_.erase(sys::lower(exe)); else assign_[sys::lower(exe)] = ch; }
        SetEvent(wake_);
    }
    void micTest() {
        if (test_.load() == TestIdle) { testAt_ = 0; test_ = TestRecord; } else test_ = TestIdle;
        SetEvent(wake_);
    }
    std::wstring devicesText() { std::lock_guard lock(mu_); return devicesText_; }
    std::wstring sessionsText() { std::lock_guard lock(mu_); return sessionsText_; }

private:
    enum { TestIdle, TestRecord, TestPlay };

    const DWORD onlyPid_; // tests: never touch any other process
    sys::Journal journal_;
    HANDLE wake_, mutex_ = nullptr;
    std::thread control_;
    std::atomic<bool> quit_{false};

    std::mutex mu_; // control and UI only, never taken by an audio thread
    std::wstring wanted_[ROLES], devicesText_, sessionsText_;
    std::map<std::wstring, int> assign_;

    std::unique_ptr<Source> sources_[SOURCES];
    std::map<DWORD, ULONGLONG> failedUntil_;
    std::map<DWORD, std::wstring> paths_;

    graph::Mixer mixer_;
    graph::Mixer::Io io_{};
    graph::MicChain micChain_;
    dsp::Fifo micMix_{2, 480, 2400}, micOut_{2, 480, 2400};
    dsp::Fifo streamOut_{2, 480, 2400};
    dsp::Fifo ownOut_[4]{{2, 480, 2400}, {2, 480, 2400}, {2, 480, 2400}, {2, 480, 2400}};
    float appBlock_[dsp::SURROUND * dsp::MAX_BLOCK]{};
    float micGain_ = 0;
    std::atomic<bool> separate_[4]{};

    std::vector<float> recording_ = std::vector<float>(2 * 48000 * 4);
    std::atomic<int> test_{TestIdle}, testAt_{0};

    wasapi::Stream main_, streamSink_, micSink_, ownSink_[4], micIn_;
    std::map<wasapi::Stream*, ULONGLONG> retryAt_;

    // ---------------------------------------------------------- audio threads

    void onApp(Source& s, float* a, int n) {
        const int total = dsp::SURROUND * n;
        const float makeup = s.makeup.load(std::memory_order_relaxed);
        // A stream that is not attenuated yet would come out 80 dB too loud. Drop it instead.
        if (makeup > 0 && dsp::peak(a, total) * makeup > 2.f) {
            if (!s.hold) state.meter[MGuardTrips].store(state.meter[MGuardTrips].load() + 1);
            s.hold = 9600;
        }
        if (makeup <= 0 || s.hold) {
            s.hold = std::max(0, s.hold - n);
            s.gain = 0;
            std::memset(a, 0, sizeof(float) * total);
        } else {
            for (int i = 0; i < n; i++) {
                if (s.gain < 1) s.gain = std::min(1.f, s.gain + 1.f / 1440);
                for (int c = 0; c < dsp::SURROUND; c++) a[dsp::SURROUND * i + c] *= makeup * s.gain;
            }
        }
        s.fifo.push(a, n);
    }

    void onMainRender(float* out, int n) {
        auto& io = io_;
        for (auto& a : io.app) std::memset(a, 0, sizeof(float) * dsp::SURROUND * n);
        for (auto& p : sources_) {
            if (!p->live.load(std::memory_order_acquire)) continue;
            if (!p->fifo.pop(appBlock_, n)) continue;
            float* dst = io.app[p->channel.load(std::memory_order_relaxed)];
            for (int i = 0; i < dsp::SURROUND * n; i++) dst[i] += appBlock_[i];
        }
        auto read = [n](dsp::Fifo& f, float* dst, bool on, float& gain) {
            if (!on) {
                gain = 0;
                f.clear = true;
                std::memset(dst, 0, sizeof(float) * 2 * n);
                return;
            }
            if (!f.pop(dst, n)) { gain = 0; return; }
            for (int i = 0; i < n; i++) {
                gain = std::min(1.f, gain + 1.f / 480);
                dst[2 * i] *= gain; dst[2 * i + 1] *= gain;
            }
        };
        read(micMix_, io.mic, micIn_.phase == wasapi::Stream::Running && state.on(MicOn), micGain_);

        std::memset(io.playback, 0, sizeof(float) * 2 * n);
        if (test_.load() == TestPlay) {
            const int at = testAt_.load(), take = std::min(n, int(recording_.size() / 2) - at);
            if (take > 0) std::memcpy(io.playback, &recording_[2 * at], sizeof(float) * 2 * take);
            if (take < n) test_ = TestIdle; else testAt_ = at + take;
        }
        for (int c = 0; c < 4; c++) io.separate[c] = separate_[c].load();
        mixer_.run(state, io, n);
        if (streamSink_.phase == wasapi::Stream::Running) streamOut_.push(io.stream, n);
        for (int c = 0; c < 4; c++) if (io.separate[c]) ownOut_[c].push(io.own[c], n);
        std::memcpy(out, io.main, sizeof(float) * 2 * n);
    }

    void onMic(float* a, int n) {
        micChain_.run(state, a, n);
        micMix_.push(a, n);
        if (micSink_.phase == wasapi::Stream::Running) micOut_.push(a, n);
        if (test_.load() == TestRecord) {
            const int at = testAt_.load(), take = std::min(n, int(recording_.size() / 2) - at);
            if (take > 0) std::memcpy(&recording_[2 * at], a, sizeof(float) * 2 * take);
            if (take < n) { testAt_ = 0; test_ = TestPlay; } else testAt_ = at + take;
        }
    }

    // ---------------------------------------------------------- control thread

    template <class Open> void ensure(wasapi::Stream& s, const std::wstring& key, Open open) {
        if (key.empty()) {
            if (s.phase != wasapi::Stream::Stopped) s.stop();
            retryAt_.erase(&s);
            return;
        }
        const ULONGLONG now = GetTickCount64();
        const bool changed = s.key != key, failed = s.phase == wasapi::Stream::Failed;
        if (!changed && !failed && s.phase != wasapi::Stream::Stopped) return;
        if (!changed && retryAt_[&s] > now) return;
        s.stop();
        s.key = key;
        retryAt_[&s] = now + 1500;
        open();
    }

    void syncStreams(const std::vector<sys::Device>& devs) {
        std::wstring want[ROLES];
        { std::lock_guard lock(mu_); std::copy(std::begin(wanted_), std::end(wanted_), want); }
        auto present = [&](const std::wstring& id, bool input) {
            return std::any_of(devs.begin(), devs.end(), [&](auto& d) { return d.input == input && d.id == id; });
        };
        for (int r = 0; r < ROLES; r++) if (!want[r].empty() && !present(want[r], r >= InMic)) want[r].clear();
        if (want[OutMain].empty()) want[OutMain] = sys::pickOutput(devs);
        if (want[InMic].empty()) want[InMic] = sys::pickInput(devs);
        if (quit_) for (auto& w : want) w.clear();

        const bool mixer = state.on(MixerOn), mic = state.on(MicOn);
        const bool testing = test_.load() != TestIdle;
        const auto& out = want[OutMain];

        ensure(main_, mixer || mic || testing || state.on(SpatialTest) ? out : L"", [&] {
            main_.render(out, [this](float* a, int n) { onMainRender(a, n); });
        });
        const auto stream = mixer && state.on(StreamerOn) && want[OutStream] != out ? want[OutStream] : L"";
        ensure(streamSink_, stream, [&] {
            streamOut_.clear = true;
            streamSink_.render(stream, [this](float* a, int n) { streamOut_.pop(a, n); });
        });
        for (int c = 0; c < 4; c++) {
            const auto id = mixer && want[OutGame + c] != out ? want[OutGame + c] : L"";
            ensure(ownSink_[c], id, [&] {
                ownOut_[c].clear = true;
                ownSink_[c].render(id, [this, c](float* a, int n) { ownOut_[c].pop(a, n); });
            });
            separate_[c] = ownSink_[c].phase == wasapi::Stream::Running;
        }
        const auto micId = mic || testing ? want[InMic] : L"";
        ensure(micIn_, micId, [&] {
            micMix_.clear = true;
            micIn_.captureDevice(micId, [this](float* a, int n) { onMic(a, n); });
        });
        const auto micOut = mic && want[OutMic] != out ? want[OutMic] : L"";
        ensure(micSink_, micOut, [&] {
            micOut_.clear = true;
            micSink_.render(micOut, [this](float* a, int n) { micOut_.pop(a, n); });
        });
        const bool mainRunning = main_.phase == wasapi::Stream::Running;
        state.meter[MMainRunning].store(mainRunning);
        state.meter[MMicRunning].store(micIn_.phase == wasapi::Stream::Running);
        state.meter[MTestState].store(float(test_.load()));
    }

    Source* sourceOf(DWORD pid) {
        for (auto& s : sources_) if (s->pid == pid) return s.get();
        return nullptr;
    }
    void release(Source& s) {
        s.makeup = 0;
        s.live = false;
        s.stream.stop();
        s.pid = 0;
    }

    void syncSessions() {
        auto sessions = sys::listSessions();
        if (onlyPid_) std::erase_if(sessions, [&](auto& s) { return s.pid != onlyPid_; });
        sys::Processes procs;
        const ULONGLONG now = GetTickCount64();
        const DWORD self = GetCurrentProcessId();
        std::map<std::wstring, int> assigned;
        { std::lock_guard lock(mu_); assigned = assign_; }

        struct App {
            std::wstring exe;
            std::vector<DWORD> up;
            int channel = -1;
            DWORD owner = 0;
            bool active = false, sharesTree = false;
            AppState st = Direct;
        };
        std::map<DWORD, App> apps;
        for (auto& s : sessions) {
            if (s.pid == self || !procs.all.count(s.pid)) continue;
            auto& a = apps[s.pid];
            a.active |= s.active;
            if (!a.exe.empty()) continue;
            a.exe = procs.exe(s.pid);
            a.up = procs.ancestors(s.pid);
            if (std::any_of(std::begin(NEVER), std::end(NEVER), [&](auto* n) { return a.exe == n; })) continue;
            if (auto it = assigned.find(a.exe); it != assigned.end()) a.channel = it->second;
            else {
                if (!paths_.count(s.pid)) paths_[s.pid] = sys::Processes::path(s.pid);
                a.channel = classify(a.exe, paths_[s.pid]);
            }
        }
        std::erase_if(paths_, [&](auto& p) { return !procs.all.count(p.first); });

        // Process loopback always captures a whole process tree. A parent of some other app,
        // or of Duyu itself, must therefore never be captured.
        for (DWORD a : procs.ancestors(self)) if (apps.count(a)) apps[a].sharesTree = true;
        for (auto& [pid, a] : apps)
            for (DWORD up : a.up)
                if (auto it = apps.find(up); it != apps.end() && it->second.exe != a.exe) it->second.sharesTree = true;

        const bool active = !quit_ && state.on(MixerOn) && main_.phase == wasapi::Stream::Running;
        for (auto& [pid, a] : apps) {
            if (a.sharesTree) { a.st = SharedTree; continue; }
            if (a.channel < 0) continue;
            a.st = Idle;
            if (!active) continue;
            a.owner = pid;
            for (DWORD up : a.up)
                if (auto it = apps.find(up); it != apps.end() && it->second.exe == a.exe && !it->second.sharesTree) a.owner = up;
        }

        std::map<DWORD, int> desired; // process to capture -> channel
        for (auto& [pid, a] : apps) if (a.owner) desired[a.owner] = apps[a.owner].channel;

        for (auto& s : sources_) {
            if (!s->pid) continue;
            const bool failed = s->stream.phase == wasapi::Stream::Failed;
            if (failed) failedUntil_[s->pid] = now + 5000;
            if (failed || !desired.count(s->pid) || (s->born && procs.birth(s->pid) != s->born)) release(*s);
        }
        for (auto [pid, ch] : desired) {
            if (auto* s = sourceOf(pid)) { s->channel = ch; continue; }
            if (failedUntil_.count(pid) && failedUntil_[pid] > now) continue;
            Source* free = sourceOf(0);
            if (!free) break;
            free->pid = pid;
            free->born = procs.birth(pid);
            free->channel = ch;
            free->settled = now + 150;
            free->fifo.clear = true;
            free->stream.captureProcess(pid, [this, free](float* a, int n) { onApp(*free, a, n); });
            free->live = true;
        }

        auto keep = [&](const sys::Session& s) {
            auto it = apps.find(s.pid);
            if (it == apps.end() || !it->second.owner) return false;
            auto* src = sourceOf(it->second.owner);
            return src && src->stream.phase == wasapi::Stream::Running;
        };
        for (auto& s : sessions) {
            if (!keep(s)) continue;
            auto* src = sourceOf(apps[s.pid].owner);
            if (s.tamed() && std::all_of(s.now.begin(), s.now.end(), sys::isTamed)) { journal_.remember(s); continue; }
            src->makeup = 0; // silence the copy before the original changes level
            if (!journal_.remember(s) || !s.set(std::vector<float>(s.now.size(), sys::TAME))) {
                failedUntil_[src->pid] = now + 5000;
                release(*src); // repair() below hands this app back to Windows
                continue;
            }
            src->settled = now + 150;
        }
        journal_.repair(sessions, keep);

        int captured = 0;
        for (auto& s : sources_) {
            if (!s->pid) continue;
            if (s->stream.phase == wasapi::Stream::Running && now >= s->settled) {
                s->makeup = 1.f / sys::TAME;
                captured++;
            }
        }
        state.meter[MCaptured].store(float(captured));
        state.meter[MMixerRunning].store(active);
        state.meter[MPending].store(float(journal_.entries.size()));

        std::wstring text;
        for (auto& [pid, a] : apps) {
            if (a.exe == L"audiodg.exe" || a.exe == L"svchost.exe" || a.exe == L"duyu.exe") continue;
            AppState st = a.st;
            if (a.owner) {
                auto* src = sourceOf(a.owner);
                st = !src ? Error : src->makeup.load() > 0 ? Captured : Starting;
            }
            text += a.exe + L'\t' + std::to_wstring(pid) + L'\t' + std::to_wstring(a.channel) + L'\t'
                  + std::to_wstring(int(st)) + L'\t' + std::to_wstring(int(a.active)) + L'\n';
        }
        std::lock_guard lock(mu_);
        sessionsText_ = std::move(text);
    }

    void control() {
        wasapi::Com com;
        std::vector<sys::Device> devs;
        ULONGLONG devicesAt = 0, sessionsAt = 0;
        bool mixerWasOn = false;
        while (!quit_) {
            const ULONGLONG now = GetTickCount64();
            const bool busy = state.on(MixerOn) || state.on(MicOn) || test_.load() != TestIdle;
            if (now >= devicesAt) {
                devs = sys::listDevices();
                std::wstring text;
                for (auto& d : devs)
                    text += std::to_wstring(int(d.input)) + L'\t' + d.id + L'\t' + d.name + L'\t' + std::to_wstring(d.form) + L'\t'
                          + std::to_wstring(int(d.isVirtual)) + L'\t' + std::to_wstring(int(d.isDefault)) + L'\t' + std::to_wstring(d.rate) + L'\n';
                { std::lock_guard lock(mu_); devicesText_ = std::move(text); }
                devicesAt = now + 3000;
            }
            syncStreams(devs);
            if (state.on(MixerOn) || mixerWasOn || now >= sessionsAt) {
                syncSessions();
                sessionsAt = GetTickCount64() + 1500;
            }
            mixerWasOn = state.on(MixerOn);
            WaitForSingleObject(wake_, state.on(MixerOn) ? 150 : busy ? 300 : 700);
        }
        syncSessions(); // give every app back before the outputs go away
        syncStreams(devs);
        for (auto& s : sources_) release(*s);
    }
};

int copyOut(const std::wstring& s, wchar_t* buf, int cap) {
    if (buf && cap > int(s.size())) std::memcpy(buf, s.c_str(), (s.size() + 1) * sizeof(wchar_t));
    return int(s.size()) + 1;
}
} // namespace

#define API extern "C" __declspec(dllexport)

API void* duyu_create(const wchar_t* dataDir, uint32_t onlyPid) {
    try { return new Engine(dataDir ? dataDir : L".", onlyPid); } catch (...) { return nullptr; }
}
API void duyu_destroy(void* e) { delete static_cast<Engine*>(e); }
API void duyu_set(void* e, int id, int index, float v) { static_cast<Engine*>(e)->set(id, index, v); }
API void duyu_set_band(void* e, int ch, int band, float on, float type, float f, float gain, float q) {
    static_cast<Engine*>(e)->setBand(ch, band, on, type, f, gain, q);
}
API void duyu_set_device(void* e, int role, const wchar_t* id) { static_cast<Engine*>(e)->setDevice(role, id); }
API void duyu_assign(void* e, const wchar_t* exe, int ch) { static_cast<Engine*>(e)->assign(exe, ch); }
API void duyu_mic_test(void* e) { static_cast<Engine*>(e)->micTest(); }
API void duyu_meters(void* e, float* out, int count) {
    for (int i = 0; i < std::min(count, int(METERS)); i++) out[i] = static_cast<Engine*>(e)->state.meter[i].load();
}
API int duyu_devices(void* e, wchar_t* buf, int cap) { return copyOut(static_cast<Engine*>(e)->devicesText(), buf, cap); }
API int duyu_sessions(void* e, wchar_t* buf, int cap) { return copyOut(static_cast<Engine*>(e)->sessionsText(), buf, cap); }

API float duyu_filter_db(int type, float f, float gain, float q, float at) { return dsp::Coef::make(type, f, gain, q).magnitudeDb(at); }

// Watchdog entry (Duyu.exe --guard). Outlives the app and repairs anything left attenuated.
// onlyPid is for tests.
API void duyu_guard(const wchar_t* dataDir, uint32_t parentPid, uint32_t onlyPid) {
    const std::wstring dir = dataDir ? dataDir : L".";
    if (parentPid)
        if (HANDLE p = OpenProcess(SYNCHRONIZE, FALSE, parentPid)) { WaitForSingleObject(p, INFINITE); CloseHandle(p); }
    wasapi::Com com;
    sys::Journal journal(dir + L"\\restore.journal");
    do {
        if (HANDLE m = OpenMutexW(SYNCHRONIZE, FALSE, mutexName(dir).c_str())) { CloseHandle(m); return; } // a live engine took over
        auto sessions = sys::listSessions();
        if (onlyPid) std::erase_if(sessions, [&](auto& s) { return s.pid != onlyPid; });
        journal.load();
        journal.repair(sessions, [](auto&) { return false; });
        if (journal.entries.empty()) return;
        Sleep(1000);
    } while (true);
}
