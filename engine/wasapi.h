// Event-driven WASAPI streams. Each stream owns one Pro Audio thread.
#pragma once
#include <windows.h>
#include <initguid.h> // emits the PKEY_* definitions
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <audiopolicy.h>
#include <avrt.h>
#include <functiondiscoverykeys_devpkey.h>
#include <wrl.h>
#include <immintrin.h>
#include <atomic>
#include <functional>
#include <string>
#include <thread>
#include <vector>
#include "dsp.h"

namespace wasapi {
using Microsoft::WRL::ComPtr;

struct Com {
    const HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ~Com() { if (SUCCEEDED(hr)) CoUninitialize(); }
};

struct AudioThread {
    DWORD task = 0;
    HANDLE handle;
    AudioThread() {
        _MM_SET_FLUSH_ZERO_MODE(_MM_FLUSH_ZERO_ON);
        _MM_SET_DENORMALS_ZERO_MODE(_MM_DENORMALS_ZERO_ON);
        handle = AvSetMmThreadCharacteristicsW(L"Pro Audio", &task);
    }
    ~AudioThread() { if (handle) AvRevertMmThreadCharacteristics(handle); }
};

inline WAVEFORMATEXTENSIBLE floatFormat(int ch, DWORD mask = 0) {
    WAVEFORMATEXTENSIBLE x{};
    x.Format = {WAVE_FORMAT_EXTENSIBLE, WORD(ch), 48000, DWORD(48000 * 4 * ch), WORD(4 * ch), 32, 22};
    x.Samples.wValidBitsPerSample = 32;
    x.dwChannelMask = mask ? mask : ch == 8 ? 0x63F : ch == 2 ? 3 : ch == 1 ? 4 : 0;
    x.SubFormat = {3, 0, 0x10, {0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71}};
    return x;
}

struct Activation : Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
                                                  Microsoft::WRL::FtmBase, IActivateAudioInterfaceCompletionHandler> {
    HANDLE done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    ~Activation() { CloseHandle(done); }
    STDMETHOD(ActivateCompleted)(IActivateAudioInterfaceAsyncOperation*) override { SetEvent(done); return S_OK; }
};

class Stream {
public:
    enum Phase { Stopped, Starting, Running, Failed };
    std::atomic<int> phase{Stopped};
    std::wstring key; // what this stream was opened for
    // monitor() only
    std::atomic<float> delayMs{0}; // first captured sample -> endpoint
    std::atomic<int> gaps{0}, trims{0};

    Stream() : stop_(CreateEventW(nullptr, TRUE, FALSE, nullptr)), ready_(CreateEventW(nullptr, FALSE, FALSE, nullptr)) {}
    ~Stream() { stop(); CloseHandle(stop_); CloseHandle(ready_); }
    Stream(const Stream&) = delete;
    Stream& operator=(const Stream&) = delete;

    using OnCapture = std::function<void(float*, int)>; // interleaved, at most MAX_BLOCK frames
    using OnRender = std::function<void(float*, int)>;  // stereo, at most MAX_BLOCK frames

    void captureDevice(const std::wstring& id, OnCapture cb) {
        launch([=, this] { runCapture(id, 0, cb); });
    }
    void captureProcess(DWORD pid, OnCapture cb) {
        launch([=, this] { runCapture(L"", pid, cb); });
    }
    void render(const std::wstring& id, OnRender cb) {
        launch([=, this] { runRender(id, cb); });
    }
    void monitor(const std::wstring& in, const std::wstring& out, OnCapture cb) {
        launch([=, this] { runMonitor(in, out, cb); });
    }
    void stop() {
        if (thread_.joinable()) {
            SetEvent(stop_);
            thread_.join();
        }
        phase = Stopped;
        key.clear();
    }

private:
    HANDLE stop_, ready_;
    std::thread thread_;

    void launch(std::function<void()> body) {
        if (thread_.joinable()) { SetEvent(stop_); thread_.join(); }
        ResetEvent(stop_);
        phase = Starting;
        thread_ = std::thread([this, body] {
            Com com;
            AudioThread priority;
            body();
            if (WaitForSingleObject(stop_, 0) != WAIT_OBJECT_0) phase = Failed;
        });
    }

    static ComPtr<IMMDevice> device(const std::wstring& id) {
        ComPtr<IMMDeviceEnumerator> en;
        ComPtr<IMMDevice> d;
        if (SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&en))))
            en->GetDevice(id.c_str(), &d);
        return d;
    }

    void runCapture(const std::wstring& id, DWORD pid, const OnCapture& cb) {
        ComPtr<IAudioClient> client;
        const int ch = pid ? dsp::SURROUND : 2;
        DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
        if (pid) {
            AUDIOCLIENT_ACTIVATION_PARAMS ap{};
            ap.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
            ap.ProcessLoopbackParams = {pid, PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE};
            PROPVARIANT pv{};
            pv.vt = VT_BLOB;
            pv.blob = {sizeof ap, reinterpret_cast<BYTE*>(&ap)};
            auto handler = Microsoft::WRL::Make<Activation>();
            ComPtr<IActivateAudioInterfaceAsyncOperation> op;
            if (FAILED(ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, __uuidof(IAudioClient), &pv, handler.Get(), &op))) return;
            if (WaitForSingleObject(handler->done, 3000) != WAIT_OBJECT_0) return;
            HRESULT hr = E_FAIL;
            ComPtr<IUnknown> unknown;
            if (FAILED(op->GetActivateResult(&hr, &unknown)) || FAILED(hr) || FAILED(unknown.As(&client))) return;
            flags |= AUDCLNT_STREAMFLAGS_LOOPBACK;
        } else {
            auto dev = device(id);
            if (!dev || FAILED(dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, &client))) return;
            flags |= AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        }
        auto fmt = floatFormat(ch);
        if (FAILED(client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, pid ? 2000000 : 0, 0, &fmt.Format, nullptr))) return;
        if (FAILED(client->SetEventHandle(ready_))) return;
        ComPtr<IAudioCaptureClient> cap;
        if (FAILED(client->GetService(IID_PPV_ARGS(&cap))) || FAILED(client->Start())) return;
        phase = Running;

        float block[dsp::SURROUND * dsp::MAX_BLOCK];
        const HANDLE waits[] = {stop_, ready_};
        for (;;) {
            if (WaitForMultipleObjects(2, waits, FALSE, 100) == WAIT_OBJECT_0) break;
            UINT32 packet = 0;
            HRESULT hr;
            while (SUCCEEDED(hr = cap->GetNextPacketSize(&packet)) && packet) {
                BYTE* data; UINT32 frames; DWORD f;
                if (FAILED(hr = cap->GetBuffer(&data, &frames, &f, nullptr, nullptr))) break;
                for (UINT32 at = 0; at < frames; at += dsp::MAX_BLOCK) {
                    const int n = int(std::min<UINT32>(dsp::MAX_BLOCK, frames - at));
                    if (f & AUDCLNT_BUFFERFLAGS_SILENT) std::memset(block, 0, sizeof(float) * ch * n);
                    else std::memcpy(block, reinterpret_cast<float*>(data) + at * ch, sizeof(float) * ch * n);
                    cb(block, n);
                }
                cap->ReleaseBuffer(frames);
            }
            if (FAILED(hr)) break;
        }
        client->Stop();
    }

    // Microphone to headphones on one thread. Each packet is written the moment it arrives, so the
    // only buffers left are those of the Windows engine.
    // shortcut: no resampling between the two clocks, add it when mic and headphones are separate devices.
    void runMonitor(const std::wstring& inId, const std::wstring& outId, const OnCapture& cb) {
        auto inDev = device(inId), outDev = device(outId);
        if (!inDev || !outDev) return;
        ComPtr<IAudioClient> in, out;
        if (FAILED(inDev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, &in))) return;
        if (FAILED(outDev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, &out))) return;
        for (auto& c : {in, out}) {
            ComPtr<IAudioClient2> c2;
            AudioClientProperties raw{sizeof raw, FALSE, AudioCategory_Other, AUDCLNT_STREAMOPTIONS_RAW};
            if (SUCCEEDED(c.As(&c2))) c2->SetClientProperties(&raw); // skips driver effects, optional
        }
        WAVEFORMATEX* mix = nullptr;
        if (FAILED(out->GetMixFormat(&mix))) return;
        const int ch = mix->nChannels;
        const DWORD mask = mix->wFormatTag == WAVE_FORMAT_EXTENSIBLE ? reinterpret_cast<WAVEFORMATEXTENSIBLE*>(mix)->dwChannelMask : 0;
        CoTaskMemFree(mix);
        auto inFmt = floatFormat(2), outFmt = floatFormat(ch, mask);
        const DWORD convert = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        if (FAILED(in->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_EVENTCALLBACK | convert, 0, 0, &inFmt.Format, nullptr))) return;
        if (FAILED(out->Initialize(AUDCLNT_SHAREMODE_SHARED, convert, 500000, 0, &outFmt.Format, nullptr))) return;
        if (FAILED(in->SetEventHandle(ready_))) return;
        ComPtr<IAudioCaptureClient> cap;
        ComPtr<IAudioRenderClient> ren;
        ComPtr<IAudioClock> clock;
        UINT32 capacity = 0;
        UINT64 rate = 0;
        if (FAILED(in->GetService(IID_PPV_ARGS(&cap))) || FAILED(out->GetService(IID_PPV_ARGS(&ren))) ||
            FAILED(out->GetService(IID_PPV_ARGS(&clock))) || FAILED(clock->GetFrequency(&rate)) || !rate ||
            FAILED(out->GetBufferSize(&capacity)) || FAILED(out->Start()) || FAILED(in->Start())) return;
        LARGE_INTEGER hz;
        QueryPerformanceFrequency(&hz);
        phase = Running;

        float block[2 * dsp::MAX_BLOCK], last[2]{};
        uint64_t written = 0;
        UINT32 spare = UINT32_MAX; // least that was still queued when a packet arrived
        int seen = 0, window = 2 * 48000, fade = 0;
        const HANDLE waits[] = {stop_, ready_};
        for (;;) {
            const DWORD w = WaitForMultipleObjects(2, waits, FALSE, 1000);
            if (w == WAIT_OBJECT_0 || w == WAIT_TIMEOUT) break;
            UINT32 packet = 0;
            HRESULT hr;
            while (SUCCEEDED(hr = cap->GetNextPacketSize(&packet)) && packet) {
                BYTE *data, *to;
                UINT32 frames, pad;
                DWORD f;
                UINT64 capturedAt = 0;
                if (FAILED(hr = cap->GetBuffer(&data, &frames, &f, nullptr, &capturedAt))) break;
                if (FAILED(hr = out->GetCurrentPadding(&pad))) break;
                spare = std::min(spare, pad);
                seen += int(frames);
                // A packet that stayed queued for the whole window is pure delay: skip one.
                // The window grows each time, so a jittery device settles instead of crackling.
                bool skip = pad + frames > capacity;
                if (seen >= window) {
                    if (spare >= frames) { skip = true; window = std::min(window * 2, 120 * 48000); }
                    seen = 0; spare = UINT32_MAX;
                }
                if (!pad && written) {
                    UINT64 pos = 0;
                    if (SUCCEEDED(clock->GetPosition(&pos, nullptr)) && double(pos) / double(rate) * 48000 >= double(written)) gaps++;
                }
                if (skip) { trims++; fade = 96; }
                else {
                    if (FAILED(hr = ren->GetBuffer(frames, &to))) break;
                    auto* dst = reinterpret_cast<float*>(to);
                    std::memset(dst, 0, sizeof(float) * ch * frames);
                    for (UINT32 at = 0; at < frames; at += dsp::MAX_BLOCK) {
                        const int n = int(std::min<UINT32>(dsp::MAX_BLOCK, frames - at));
                        if (f & AUDCLNT_BUFFERFLAGS_SILENT) std::memset(block, 0, sizeof(float) * 2 * n);
                        else std::memcpy(block, reinterpret_cast<float*>(data) + at * 2, sizeof(float) * 2 * n);
                        cb(block, n);
                        for (int i = 0; i < n; i++) {
                            float *s = block + 2 * i, *o = dst + (at + i) * ch;
                            if (fade) {
                                const float k = float(fade--) / 96;
                                for (int c = 0; c < 2; c++) s[c] += (last[c] - s[c]) * k;
                            } else { last[0] = s[0]; last[1] = s[1]; }
                            if (ch == 1) o[0] = .5f * (s[0] + s[1]);
                            else { o[0] = s[0]; o[1] = s[1]; }
                        }
                    }
                    if (FAILED(hr = ren->ReleaseBuffer(frames, 0))) break;

                    UINT64 pos = 0, posAt = 0;
                    if (SUCCEEDED(clock->GetPosition(&pos, &posAt)) && pos) {
                        LARGE_INTEGER t;
                        QueryPerformanceCounter(&t);
                        const double now = double(t.QuadPart) * 1000 / double(hz.QuadPart);
                        const double played = double(pos) * 1000 / double(rate) + now - double(posAt) / 10000;
                        delayMs = float(now - double(capturedAt) / 10000 + double(written) / 48 - played);
                    }
                    written += frames;
                }
                if (FAILED(hr = cap->ReleaseBuffer(frames))) break;
            }
            if (FAILED(hr)) break;
        }
        in->Stop();
        out->Stop();
    }

    void runRender(const std::wstring& id, const OnRender& cb) {
        auto dev = device(id);
        if (!dev) return;
        ComPtr<IAudioClient> client;
        if (FAILED(dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, &client))) return;
        WAVEFORMATEX* mix = nullptr;
        if (FAILED(client->GetMixFormat(&mix))) return;
        const int ch = mix->nChannels;
        const DWORD mask = mix->wFormatTag == WAVE_FORMAT_EXTENSIBLE ? reinterpret_cast<WAVEFORMATEXTENSIBLE*>(mix)->dwChannelMask : 0;
        const bool native = mix->nSamplesPerSec == 48000 && mix->wBitsPerSample == 32;
        auto fmt = floatFormat(ch, mask);

        bool ready = false;
        int period = 480;
        ComPtr<IAudioClient3> c3;
        if (native && SUCCEEDED(client.As(&c3))) {
            UINT32 def, unit, lo, hi;
            if (SUCCEEDED(c3->GetSharedModeEnginePeriod(mix, &def, &unit, &lo, &hi)) && lo < def &&
                SUCCEEDED(c3->InitializeSharedAudioStream(AUDCLNT_STREAMFLAGS_EVENTCALLBACK, lo, mix, nullptr))) {
                ready = true;
                period = int(lo);
            }
        }
        CoTaskMemFree(mix);
        if (!ready) {
            REFERENCE_TIME p = 100000;
            client->GetDevicePeriod(&p, nullptr);
            period = int(p * 48000 / 10000000);
            if (FAILED(client->Initialize(AUDCLNT_SHAREMODE_SHARED,
                    AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                    0, 0, &fmt.Format, nullptr))) return;
        }
        if (FAILED(client->SetEventHandle(ready_))) return;
        ComPtr<IAudioRenderClient> out;
        UINT32 capacity = 0;
        if (FAILED(client->GetBufferSize(&capacity)) || FAILED(client->GetService(IID_PPV_ARGS(&out)))) return;
        const UINT32 target = std::min<UINT32>(capacity, UINT32(period * 3 / 2)); // half a period of head-room

        float stereo[2 * dsp::MAX_BLOCK];
        auto fill = [&]() -> bool {
            UINT32 pad = 0;
            if (FAILED(client->GetCurrentPadding(&pad))) return false;
            if (pad >= target) return true;
            const UINT32 frames = target - pad;
            BYTE* data;
            if (FAILED(out->GetBuffer(frames, &data))) return false;
            auto* dst = reinterpret_cast<float*>(data);
            std::memset(dst, 0, sizeof(float) * ch * frames);
            for (UINT32 at = 0; at < frames; at += dsp::MAX_BLOCK) {
                const int n = int(std::min<UINT32>(dsp::MAX_BLOCK, frames - at));
                cb(stereo, n);
                for (int i = 0; i < n; i++) {
                    float* o = dst + (at + i) * ch;
                    if (ch == 1) o[0] = .5f * (stereo[2 * i] + stereo[2 * i + 1]);
                    else { o[0] = stereo[2 * i]; o[1] = stereo[2 * i + 1]; }
                }
            }
            return SUCCEEDED(out->ReleaseBuffer(frames, 0));
        };
        if (!fill() || FAILED(client->Start())) return;
        phase = Running;
        const HANDLE waits[] = {stop_, ready_};
        for (;;) {
            const DWORD w = WaitForMultipleObjects(2, waits, FALSE, 1000);
            if (w == WAIT_OBJECT_0 || w == WAIT_TIMEOUT) break;
            if (!fill()) break;
        }
        client->Stop();
    }
};
} // namespace wasapi
